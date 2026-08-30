using Kairos.Api.Features.Bookings;
using Kairos.Api.Integrations.Webhooks;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Security;

using Microsoft.EntityFrameworkCore;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The one place double-booking can happen, so the one place a sequential test would lie. The
/// first case runs two real threads against one file-backed WAL database; the other two pin the
/// conflict rule the SQL encodes — shared across links, half-open, max of the two margins.
/// </summary>
public class BookingConcurrencyTests
{
    private const int NoMargin = 0;
    private const int LargerMarginMinutes = 30;
    private const int SmallerMarginMinutes = 20;
    private const int SlotMinutes = 30;
    private const int MeetingMinutes = 60;
    private const int RacingCallers = 2;
    private const string ConsultingSlug = "consulting";
    private const string MentorshipSlug = "mentorship";

    private static readonly Guid ConsultingId = Guid.CreateVersion7();
    private static readonly Guid MentorshipId = Guid.CreateVersion7();
    private static readonly Instant MeetingStart = Instant.FromUtc(2026, 9, 14, 10, 0);
    private static readonly Duration SlotDuration = Duration.FromMinutes(SlotMinutes);
    private static readonly BookerDetails Booker = new("Ada", "ada@example.com", "Europe/Warsaw");

    /// <summary>
    /// Thirty-two zero bytes, base64. A key a test can name, and one no deployment would ever
    /// hold — <see cref="SecretProtector"/> only checks the length.
    /// </summary>
    private static readonly string TestKey = Convert.ToBase64String(new byte[SecretProtector.KeyBytes]);

    [Fact]
    public void TryCreate_TwoConcurrentRequestsForOneSlot_CreatesExactlyOne()
    {
        using var database = new TestDatabase();
        using (var seed = database.CreateContext())
        {
            Seed(seed, NoMargin);
        }

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
                var writer = Writer(context);
                barrier.SignalAndWait();
                results[slot] = writer.TryCreate(
                    ConsultingId, ConsultingSlug, MeetingStart, SlotDuration, Booker, string.Empty, NoMargin);
            });
            callers[slot].Start();
        }

        foreach (var caller in callers)
        {
            caller.Join();
        }

        results.Count(result => result.Success).ShouldBe(1);
        results.Single(result => !result.Success).Reason.ShouldBe(BookingWriter.SlotTakenReason);

        var winner = results.Single(result => result.Success);
        winner.ManageToken.ShouldNotBeNullOrWhiteSpace();

        using var read = database.CreateContext();
        read.Bookings.Count(booking => booking.Status == BookingStatus.Confirmed).ShouldBe(1);

        // The row keeps the hash, never the token. Whoever reads the database cannot use what
        // they find there to cancel the meeting.
        read.Bookings.Single().ManageTokenHash.ShouldBe(BookingWriter.Hash(winner.ManageToken!));

        // One booking, one booker. The participant row is what the confirmation email and the
        // .ics attendee are read from, so a booking without one is a booking nobody is on.
        read.BookingParticipants.Count().ShouldBe(1);
    }

    [Fact]
    public void TryCreate_OverlappingSlotOnAnotherLink_IsRejected()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        Seed(context, NoMargin);
        var writer = Writer(context);

        writer.TryCreate(ConsultingId, ConsultingSlug, MeetingStart, SlotDuration, Booker, string.Empty, NoMargin)
            .Success.ShouldBeTrue();

        // Halfway into the consulting meeting, on the other link. Availability is one calendar,
        // not one calendar per link, so the second link may not sell the same half hour.
        var overlapping = MeetingStart + Duration.FromMinutes(SlotMinutes / 2);
        var result = writer.TryCreate(
            MentorshipId, MentorshipSlug, overlapping, SlotDuration, Booker, string.Empty, NoMargin);

        result.Success.ShouldBeFalse();
        result.Reason.ShouldBe(BookingWriter.SlotTakenReason);
        context.Bookings.Count(booking => booking.Status == BookingStatus.Confirmed).ShouldBe(1);
    }

    [Fact]
    public void TryCreate_ExistingMeetingHasTheLargerMargin_BlocksTheSmallerMarginLink()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        Seed(context, LargerMarginMinutes);
        var writer = Writer(context);

        // 10:00-11:00 on the link that wants thirty minutes either side of itself.
        var existing = Duration.FromMinutes(MeetingMinutes);
        writer.TryCreate(ConsultingId, ConsultingSlug, MeetingStart, existing, Booker, string.Empty, LargerMarginMinutes)
            .Success.ShouldBeTrue();

        // Both margins are non-zero and unequal on purpose: with thirty against zero the maximum
        // and the sum are the same number, and a writer that added them would pass. Thirty
        // against twenty separates them — blocked to 11:30, free at 11:30, not 11:50.
        var insideTheLargerMargin = MeetingStart + existing + Duration.FromMinutes(SmallerMarginMinutes);
        var atTheLargerMargin = MeetingStart + existing + Duration.FromMinutes(LargerMarginMinutes);

        writer.TryCreate(MentorshipId, MentorshipSlug, insideTheLargerMargin, SlotDuration, Booker, string.Empty, SmallerMarginMinutes)
            .Reason.ShouldBe(BookingWriter.SlotTakenReason);
        writer.TryCreate(MentorshipId, MentorshipSlug, atTheLargerMargin, SlotDuration, Booker, string.Empty, SmallerMarginMinutes)
            .Success.ShouldBeTrue();
    }

    /// <summary>
    /// The writer as the composition root builds it. The outbox writer is not a stand-in: the
    /// booking and its announcement go in on one SaveChanges, so a test that left it out would
    /// exercise a shorter INSERT batch than the application ever runs.
    /// </summary>
    private static BookingWriter Writer(KairosDbContext context) =>
        new(
            context,
            SystemClock.Instance,
            new OutboxWriter(context, SystemClock.Instance),
            new FollowUpWriter(context, new SecretProtector(TestKey), SystemClock.Instance));

    private static void Seed(KairosDbContext context, int consultingMarginMinutes)
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
                MarginMinutes = consultingMarginMinutes,
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
