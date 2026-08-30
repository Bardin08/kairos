using Kairos.Api.Features.Bookings;
using Kairos.Api.Features.Invites;
using Kairos.Api.Integrations.Webhooks;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Security;

using Microsoft.EntityFrameworkCore;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// Spending an invite, which happens inside the booking's own BEGIN IMMEDIATE and nowhere else.
/// The first case runs two real threads against one file-backed WAL database, because a token two
/// callers can spend is the only failure this feature has that a sequential test cannot see.
/// </summary>
public class InviteConsumptionTests
{
    private const int NoMargin = 0;
    private const int SlotMinutes = 30;
    private const int RacingCallers = 2;

    /// <summary>Far enough apart that the two racing callers cannot collide on anything but the token.</summary>
    private const int ApartHours = 2;

    private const int OneSecond = 1;

    /// <summary>
    /// Far above the moment a thread needs to reach the barrier, since both build their context
    /// and run their migration before signalling. It is here so that a caller who never arrives —
    /// one that threw on the way — turns this test red instead of parking the run forever.
    /// </summary>
    private const int BarrierTimeoutSeconds = 30;

    private const string ConsultingSlug = "consulting";
    private const string MentorshipSlug = "mentorship";

    private static readonly Guid ConsultingId = Guid.CreateVersion7();
    private static readonly Guid MentorshipId = Guid.CreateVersion7();

    /// <summary>What the clock answers. A week before the meeting, so the two instants cannot be confused.</summary>
    private static readonly Instant Now = Instant.FromUtc(2026, 9, 14, 10, 0);

    private static readonly long NowSeconds = Now.ToUnixTimeSeconds();
    private static readonly Instant MeetingStart = Instant.FromUtc(2026, 9, 21, 10, 0);
    private static readonly Instant OtherMeetingStart = MeetingStart + Duration.FromHours(ApartHours);
    private static readonly Duration SlotDuration = Duration.FromMinutes(SlotMinutes);
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(BarrierTimeoutSeconds);
    private static readonly BookerDetails Booker = new("Ada", "ada@example.com", "Europe/Warsaw");

    /// <summary>An operator who filled nothing in: an empty bag is a valid invite that still spends once.</summary>
    private static readonly Dictionary<string, string> NoPrefill = [];

    /// <summary>
    /// Thirty-two zero bytes, base64. A key a test can name, and one no deployment would ever
    /// hold — <see cref="SecretProtector"/> only checks the length.
    /// </summary>
    private static readonly string TestKey = Convert.ToBase64String(new byte[SecretProtector.KeyBytes]);

    /// <summary>
    /// One token, two callers, two different half hours. The times are different on purpose: on
    /// one slot the loser would be refused for the slot, and a token spent twice would leave no
    /// trace — <c>slot_taken</c> would hide the failure this test exists to catch.
    /// </summary>
    [Fact]
    public void TryCreate_TwoConcurrentCallersHoldingOneInvite_SpendsItExactlyOnce()
    {
        using var database = new TestDatabase();
        string token;
        using (var seed = database.CreateContext())
        {
            Seed(seed);
            token = Issue(seed, ConsultingId, expiresAt: null).Token;
        }

        Instant[] starts = [MeetingStart, OtherMeetingStart];
        var results = new CreateBookingResult[RacingCallers];

        // Every context is built — and every migration run — before anyone books, so the two
        // threads meet at the barrier with nothing left to do but contend for the write lock.
        using var barrier = new Barrier(RacingCallers);
        var callers = new Thread[RacingCallers];
        for (var index = 0; index < callers.Length; index++)
        {
            var slot = index;
            callers[slot] = new Thread(() =>
            {
                using var context = database.CreateContext();
                var writer = Writer(context, new FixedClock(Now));
                if (!barrier.SignalAndWait(BarrierTimeout))
                {
                    // The other caller never arrived. Leaving the default result behind fails the
                    // assertions below, which is the outcome worth having: a hung run reports
                    // nothing at all.
                    return;
                }

                results[slot] = writer.TryCreate(
                    ConsultingId, ConsultingSlug, starts[slot], SlotDuration, Booker, string.Empty, NoMargin, token);
            });
            callers[slot].Start();
        }

        foreach (var caller in callers)
        {
            caller.Join();
        }

        results.Count(result => result.Success).ShouldBe(1);
        results.Single(result => !result.Success).Reason.ShouldBe(BookingWriter.InviteSpentReason);

        using var read = database.CreateContext();
        read.Bookings.Count(booking => booking.Status == BookingStatus.Confirmed).ShouldBe(1);

        // The row names the caller that won, not merely some booking: the loser's booking was
        // never written, so an invite pointing at it would point at nothing.
        var winner = results.Single(result => result.Success);
        read.PersonalInvites.Single().BookingId.ShouldBe(winner.Id);
    }

    /// <summary>
    /// Every other clause passes here — unspent, unrevoked, no deadline — so the link is the only
    /// thing standing between an invite aimed at one link and a booking on another.
    /// </summary>
    [Fact]
    public void TryCreate_AnInviteIssuedForAnotherLink_IsRefused()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        Seed(context);
        var token = Issue(context, MentorshipId, expiresAt: null).Token;

        var result = Writer(context, new FixedClock(Now)).TryCreate(
            ConsultingId, ConsultingSlug, MeetingStart, SlotDuration, Booker, string.Empty, NoMargin, token);

        result.Success.ShouldBeFalse();
        result.Reason.ShouldBe(BookingWriter.InviteSpentReason);

        using var read = database.CreateContext();
        read.Bookings.Count().ShouldBe(0);
        read.PersonalInvites.Single().ConsumedAt.ShouldBeNull();
    }

    /// <summary>
    /// The unknown token, and the case that pins the hash clause. Every other case here seeds one
    /// invite on the link being booked, so the token is never what decides — drop the hash from
    /// the predicate and they all still pass. What that leaves uncovered is the real attack: an
    /// anonymous caller posting any well-formed string would spend whatever invite the link has
    /// outstanding and walk off with its booking.
    /// </summary>
    [Fact]
    public void TryCreate_ATokenNoInviteHas_IsRefused()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        Seed(context);
        Issue(context, ConsultingId, expiresAt: null);

        // Minted the way a real one is and never stored, so it is well-formed and matches nothing.
        var (unknownToken, _) = InviteWriter.NewToken();

        var result = Writer(context, new FixedClock(Now)).TryCreate(
            ConsultingId, ConsultingSlug, MeetingStart, SlotDuration, Booker, string.Empty, NoMargin, unknownToken);

        result.Success.ShouldBeFalse();
        result.Reason.ShouldBe(BookingWriter.InviteSpentReason);

        using var read = database.CreateContext();
        read.Bookings.Count().ShouldBe(0);
        read.PersonalInvites.Single().ConsumedAt.ShouldBeNull();
    }

    /// <summary>
    /// Nothing has run to mark this row: the deadline is compared against the clock at the moment
    /// of the spend, the way every other reader of it compares, so a lapsed invite is lapsed
    /// whether or not a sweeper exists.
    /// </summary>
    [Fact]
    public void TryCreate_OneSecondAfterTheDeadline_IsRefused()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        Seed(context);
        var token = Issue(context, ConsultingId, Now).Token;

        var afterTheDeadline = Now + Duration.FromSeconds(OneSecond);
        var result = Writer(context, new FixedClock(afterTheDeadline)).TryCreate(
            ConsultingId, ConsultingSlug, MeetingStart, SlotDuration, Booker, string.Empty, NoMargin, token);

        result.Success.ShouldBeFalse();
        result.Reason.ShouldBe(BookingWriter.InviteSpentReason);

        using var read = database.CreateContext();
        read.Bookings.Count().ShouldBe(0);
        read.PersonalInvites.Single().ConsumedAt.ShouldBeNull();
    }

    [Fact]
    public void TryCreate_ARevokedInvite_IsRefused()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        Seed(context);
        var (invite, token) = Issue(context, ConsultingId, expiresAt: null);
        new InviteWriter(context, new FixedClock(Now)).Revoke(invite);
        context.SaveChanges();

        var result = Writer(context, new FixedClock(Now)).TryCreate(
            ConsultingId, ConsultingSlug, MeetingStart, SlotDuration, Booker, string.Empty, NoMargin, token);

        result.Success.ShouldBeFalse();
        result.Reason.ShouldBe(BookingWriter.InviteSpentReason);

        using var read = database.CreateContext();
        read.Bookings.Count().ShouldBe(0);
        read.PersonalInvites.Single().ConsumedAt.ShouldBeNull();
    }

    /// <summary>
    /// The ordinary booking, called with the argument list every caller used before this feature
    /// existed. An outstanding invite is seeded so that "spends nothing" is a claim about a row
    /// that was there to spend.
    /// </summary>
    [Fact]
    public void TryCreate_NoInviteToken_BooksAndSpendsNothing()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        Seed(context);
        Issue(context, ConsultingId, expiresAt: null);

        var result = Writer(context, new FixedClock(Now)).TryCreate(
            ConsultingId, ConsultingSlug, MeetingStart, SlotDuration, Booker, string.Empty, NoMargin);

        result.Success.ShouldBeTrue();
        result.ManageToken.ShouldNotBeNullOrWhiteSpace();

        using var read = database.CreateContext();
        read.Bookings.Count(booking => booking.Status == BookingStatus.Confirmed).ShouldBe(1);

        var stored = read.PersonalInvites.Single();
        stored.ConsumedAt.ShouldBeNull();
        stored.BookingId.ShouldBeNull();
    }

    /// <summary>
    /// The invite has to name the booking that actually spent it, which is only true if the
    /// identifier in the UPDATE is the identifier the booking row then gets.
    /// <see cref="PersonalInvite.BookingId"/> carries no foreign key on purpose, so nothing in the
    /// database will catch the other order.
    /// </summary>
    [Fact]
    public void TryCreate_ASpentInvite_RecordsTheBookingThatSpentIt()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        Seed(context);
        var token = Issue(context, ConsultingId, expiresAt: null).Token;

        var result = Writer(context, new FixedClock(Now)).TryCreate(
            ConsultingId, ConsultingSlug, MeetingStart, SlotDuration, Booker, string.Empty, NoMargin, token);

        result.Success.ShouldBeTrue();

        // A fresh context: the UPDATE went round the change tracker, so the instance this test
        // seeded still says nothing happened to it.
        using var read = database.CreateContext();
        var stored = read.PersonalInvites.Single();
        stored.ConsumedAt.ShouldBe(NowSeconds);
        stored.BookingId.ShouldBe(result.Id);
        read.Bookings.Single().Id.ShouldBe(result.Id);
    }

    /// <summary>
    /// The fold and the consuming predicate are one decision written in two languages, and a
    /// database cannot call a C# method, so the duplication cannot be deleted. What it can do is
    /// fail loudly: the expectation is read off <see cref="InviteWriter.StateAt"/> rather than
    /// written down here, so moving either side alone — the fold's comparison or the UPDATE's —
    /// turns this red, and moving both together is the only way to keep it green.
    /// </summary>
    [Fact]
    public void TryCreate_AtTheDeadlineSecondExactly_AgreesWithTheFold()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        Seed(context);
        var (invite, token) = Issue(context, ConsultingId, Now);

        // Read before the spend, off the row as it stands: whether the fold still calls an invite
        // open at the very second of its deadline is the whole question, and the answer here is
        // whatever the fold says rather than what this test would like it to be.
        var foldSaysOpen = InviteWriter.StateAt(invite, Now) == InviteState.Open;

        var result = Writer(context, new FixedClock(Now)).TryCreate(
            ConsultingId, ConsultingSlug, MeetingStart, SlotDuration, Booker, string.Empty, NoMargin, token);

        result.Success.ShouldBe(foldSaysOpen);
    }

    /// <summary>
    /// The placement, not the atomicity. A single atomic UPDATE issued outside the booking's
    /// transaction would also spend a token exactly once, so the race above cannot tell the two
    /// designs apart — this can. The insert is made to fail after the invite is already spent, and
    /// the invite has to come back unspent, which is only true while the UPDATE is inside the
    /// transaction that rolls back.
    /// </summary>
    [Fact]
    public void TryCreate_TheInsertFailingAfterTheSpend_RollsTheSpendBackWithIt()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        Seed(context);
        var token = Issue(context, ConsultingId, expiresAt: null).Token;

        // Null notes against a NOT NULL column, which is the only lever that fails the write after
        // the spend: every refusal the writer decides for itself returns before it.
        Should.Throw<DbUpdateException>(() => Writer(context, new FixedClock(Now)).TryCreate(
            ConsultingId, ConsultingSlug, MeetingStart, SlotDuration, Booker, null!, NoMargin, token));

        using var read = database.CreateContext();
        read.PersonalInvites.Single().ConsumedAt.ShouldBeNull();
        read.PersonalInvites.Single().BookingId.ShouldBeNull();
        read.Bookings.Count().ShouldBe(0);
    }

    /// <summary>
    /// The writer as the composition root builds it, taking the clock the case needs. The outbox
    /// writer is not a stand-in: the booking and its announcement go in on one SaveChanges, so a
    /// test that left it out would exercise a shorter INSERT batch than the application ever runs.
    /// </summary>
    private static BookingWriter Writer(KairosDbContext context, IClock clock) =>
        new(
            context,
            clock,
            new OutboxWriter(context, clock),
            new FollowUpWriter(context, new SecretProtector(TestKey), clock));

    /// <summary>Through the real issuing path, so the token these cases spend is minted the way one in a URL is.</summary>
    private static (PersonalInvite Invite, string Token) Issue(
        KairosDbContext context,
        Guid linkId,
        Instant? expiresAt)
    {
        var result = new InviteWriter(context, new FixedClock(Now)).Issue(linkId, NoPrefill, expiresAt);
        context.SaveChanges();
        return (result.Invite!, result.Token!);
    }

    private static void Seed(KairosDbContext context)
    {
        var pattern = new Pattern { Id = Guid.CreateVersion7(), Name = "Weekdays" };
        context.Patterns.Add(pattern);
        context.Links.AddRange(
            new Link
            {
                Id = ConsultingId,
                Slug = ConsultingSlug,
                Name = "Consulting",
                PatternId = pattern.Id,
                MarginMinutes = NoMargin,
            },
            new Link
            {
                Id = MentorshipId,
                Slug = MentorshipSlug,
                Name = "Mentorship",
                PatternId = pattern.Id,
                MarginMinutes = NoMargin,
            });
        context.SaveChanges();
    }
}
