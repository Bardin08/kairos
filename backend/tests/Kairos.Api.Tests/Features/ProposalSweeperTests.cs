using Kairos.Api.Features.Bookings;
using Kairos.Api.Integrations.Email;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using MimeKit;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// Which proposals the sweeper closes, how many times the operator hears about each, and the order
/// the two writes happen in — which is the whole of its failure story.
/// </summary>
/// <remarks>
/// The work method is driven directly rather than through the timer. What the loop adds is a
/// cadence, and a test that started the service and waited for a sweep would be asserting on a
/// clock — the same test written twice a minute apart, one of which is flaky. The clock is fixed
/// for the same reason, and because the expiry is a half-open bound whose edge is only reachable
/// with a clock a test can name.
///
/// The relay is substituted at the socket, which needs a different argument from the one
/// <see cref="EmailSender"/>'s own remarks make: that carve-out is for a caller that acts on the
/// answer, and this one discards it. What is being observed here is not the answer but the calls —
/// how many were made, and what was on disk while each was in flight — and neither is visible from
/// anywhere else. Everything above the socket is real: the sweeper resolves its own scope, its own
/// context and its own mailer out of a container built the way the application builds one.
/// </remarks>
public class ProposalSweeperTests
{
    private const int OneHourSeconds = 60 * 60;
    private const int OneDaySeconds = 24 * OneHourSeconds;
    private const int MeetingSeconds = 30 * 60;

    /// <summary>An expiry the clock has already passed, which is the whole trigger.</summary>
    private const int LapsedAnHourAgo = -OneHourSeconds;

    /// <summary>
    /// The edge of the half-open bound. A hold expiring at the very instant a sweep reads the
    /// clock has lapsed — <c>ExpiresAt &lt;= now</c> — and every other reader in this feature
    /// draws the line in the same place.
    /// </summary>
    private const int LapsesOnThisTick = 0;

    private const int LapsesTomorrow = OneDaySeconds;

    private const int NoNotices = 0;
    private const int OneNotice = 1;
    private const int TwoNotices = 2;

    /// <summary>Which send the refusing relay refuses. One-based, so this is the first.</summary>
    private const int FirstAttempt = 1;

    private const string PatternName = "Weekdays";
    private const string Slug = "consulting";
    private const string LinkName = "Consulting call";
    private const string BookerName = "Ada Lovelace";
    private const string BookerEmail = "ada@example.com";
    private const string BookerZone = "Europe/Warsaw";

    private const string RelayHost = "smtp.example.com";
    private const string RelayFrom = "organizer@example.com";
    private const string RelayBaseUrl = "https://kairos.example.com";

    /// <summary>What a relay answers a message it took. Never read; it only has to exist.</summary>
    private const string RelayAccepted = "2.0.0 Ok: queued";

    private const string RelayRefused = "The relay dropped the connection.";

    /// <summary>
    /// The instant every row here is written relative to, and the one the sweeper reads. Fixed, so
    /// that "expires on this tick" is a state a test can actually construct.
    /// </summary>
    private static readonly Instant Now = Instant.FromUtc(2026, 9, 7, 9, 0);

    /// <summary>
    /// Twice through the sweeper, one message. <see cref="BookingProposal.NotifiedAt"/> is what
    /// makes the second tick a no-op, and without it a lapse the operator has already been told
    /// about is mailed again every minute for the life of the deployment.
    /// </summary>
    [Fact]
    public async Task Sweep_AProposalWhoseHoldHasLapsed_IsMarkedExpiredAndTheOperatorIsToldOnce()
    {
        using var database = new TestDatabase();
        var relay = new CountingRelay();
        await using var provider = NewHost(database, relay);
        var proposal = SeedProposal(database, LapsedAnHourAgo);

        var sweeper = NewSweeper(provider);
        await sweeper.SweepAsync(TestContext.Current.CancellationToken);
        await sweeper.SweepAsync(TestContext.Current.CancellationToken);

        using var context = database.CreateContext();
        var swept = context.BookingProposals.Single(row => row.Id == proposal);
        swept.State.ShouldBe(ProposalState.Expired);
        swept.DecidedAt.ShouldNotBeNull();
        swept.NotifiedAt.ShouldNotBeNull();
        relay.Sent.ShouldBe(OneNotice);
    }

    /// <summary>
    /// The edge, which is where a half-open bound goes wrong. A hold that expires at the instant
    /// this tick reads the clock has lapsed, because that is what every other reader of
    /// <see cref="BookingProposal.ExpiresAt"/> in this feature decides — and a sweeper drawing the
    /// line one second later than the conflict readers would leave a slot free to book and a
    /// suggestion still open over it.
    /// </summary>
    [Fact]
    public async Task Sweep_AHoldExpiringOnThisVeryTick_HasLapsed()
    {
        using var database = new TestDatabase();
        var relay = new CountingRelay();
        await using var provider = NewHost(database, relay);
        var proposal = SeedProposal(database, LapsesOnThisTick);

        await NewSweeper(provider).SweepAsync(TestContext.Current.CancellationToken);

        using var context = database.CreateContext();
        context.BookingProposals.Single(row => row.Id == proposal).State.ShouldBe(ProposalState.Expired);
        relay.Sent.ShouldBe(OneNotice);
    }

    [Fact]
    public async Task Sweep_AnOpenProposalStillInDate_IsLeftAlone()
    {
        using var database = new TestDatabase();
        var relay = new CountingRelay();
        await using var provider = NewHost(database, relay);
        var proposal = SeedProposal(database, LapsesTomorrow);

        await NewSweeper(provider).SweepAsync(TestContext.Current.CancellationToken);

        using var context = database.CreateContext();
        context.BookingProposals.Single(row => row.Id == proposal).State.ShouldBe(ProposalState.Open);
        relay.Sent.ShouldBe(NoNotices);
    }

    /// <summary>
    /// The design's whole failure story, pinned on the one thing that makes it true: the message
    /// goes out while the row still says nobody has been told. A tick that dies in the gap is
    /// therefore re-sent by the next one rather than lost, which is the at-least-once direction
    /// this notice is deliberately biased in.
    /// </summary>
    /// <remarks>
    /// Read through a second connection, so what is asserted is what is committed at the moment of
    /// the send rather than what the sweeper's own change tracker is holding. Both halves matter:
    /// Expired says the first save has landed, and a null marker says the second has not.
    /// </remarks>
    [Fact]
    public async Task Sweep_Always_SendsBeforeItMarksTheRowNotified()
    {
        using var database = new TestDatabase();
        var relay = new ObservingRelay(database);
        await using var provider = NewHost(database, relay);
        SeedProposal(database, LapsedAnHourAgo);

        await NewSweeper(provider).SweepAsync(TestContext.Current.CancellationToken);

        relay.Rows.ShouldHaveSingleItem().ShouldBe((ProposalState.Expired, null));
    }

    /// <summary>
    /// The state a tick that died in that gap leaves behind, and the reason the query asks about
    /// <see cref="BookingProposal.NotifiedAt"/> rather than about the state alone: the row is
    /// already Expired, the message never went, and the next tick is the only thing that will ever
    /// fix that.
    /// </summary>
    [Fact]
    public async Task Sweep_AnExpiredProposalNobodyWasToldAbout_IsPickedUpByTheNextTick()
    {
        using var database = new TestDatabase();
        var relay = new CountingRelay();
        await using var provider = NewHost(database, relay);
        var proposal = SeedProposal(database, LapsedAnHourAgo, ProposalState.Expired);

        await NewSweeper(provider).SweepAsync(TestContext.Current.CancellationToken);

        using var context = database.CreateContext();
        context.BookingProposals.Single(row => row.Id == proposal).NotifiedAt.ShouldNotBeNull();
        relay.Sent.ShouldBe(OneNotice);
    }

    /// <summary>
    /// Answered is answered, however long ago. A decline leaves the row in place with its expiry
    /// still running out underneath it, so a sweeper that looked only at the clock would reopen a
    /// closed question and tell the operator nobody had replied to a suggestion that was refused.
    /// </summary>
    [Fact]
    public async Task Sweep_ADeclinedProposalWhoseExpiryHasSincePassed_IsLeftAlone()
    {
        using var database = new TestDatabase();
        var relay = new CountingRelay();
        await using var provider = NewHost(database, relay);
        var proposal = SeedProposal(database, LapsedAnHourAgo, ProposalState.Declined);

        await NewSweeper(provider).SweepAsync(TestContext.Current.CancellationToken);

        using var context = database.CreateContext();
        context.BookingProposals.Single(row => row.Id == proposal).State.ShouldBe(ProposalState.Declined);
        relay.Sent.ShouldBe(NoNotices);
    }

    /// <summary>
    /// One bad row must not strand the rest of the sweep. The relay refuses the first message and
    /// takes the second; both holds still close, both are marked told, and the second tick sends
    /// nothing at all.
    /// </summary>
    /// <remarks>
    /// The refusal is absorbed inside <see cref="OperatorMailer"/> — rule 14 — so what this pins
    /// is that the sweep never sees it and never has to. Without that swallow the throw leaves the
    /// loop, the first row keeps a null marker, and every row after it in the batch is untouched
    /// until a tick happens to run when the relay is healthy.
    /// </remarks>
    [Fact]
    public async Task Sweep_ARelayThatRefusesTheFirstOfTwo_ClosesBothAndRetriesNeither()
    {
        using var database = new TestDatabase();
        var relay = new RefusingFirstRelay();
        await using var provider = NewHost(database, relay);
        SeedProposal(database, LapsedAnHourAgo);
        SeedProposal(database, LapsedAnHourAgo);

        var sweeper = NewSweeper(provider);
        await sweeper.SweepAsync(TestContext.Current.CancellationToken);
        await sweeper.SweepAsync(TestContext.Current.CancellationToken);

        using var context = database.CreateContext();
        var swept = context.BookingProposals.AsNoTracking().ToList();
        swept.ShouldAllBe(row => row.State == ProposalState.Expired);
        swept.ShouldAllBe(row => row.NotifiedAt != null);
        relay.Attempts.ShouldBe(TwoNotices);
    }

    private static ProposalSweeper NewSweeper(ServiceProvider provider) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(Now),
            NullLogger<ProposalSweeper>.Instance);

    /// <summary>
    /// The container the sweeper resolves through, built the way the application builds one:
    /// options constructed by hand and registered as a singleton, so the process keeps exactly one
    /// EF model. <see cref="Integrations.MeetingEndedPollerTests"/> records what AddDbContext costs
    /// here.
    /// </summary>
    private static ServiceProvider NewHost(TestDatabase database, EmailSender relay)
    {
        var services = new ServiceCollection();
        services.AddSingleton(
            new DbContextOptionsBuilder<KairosDbContext>().UseSqlite(database.ConnectionString).Options);
        services.AddScoped<KairosDbContext>();
        services.AddSingleton<IClock>(new FixedClock(Now));
        services.AddSingleton<ILogger<OperatorMailer>>(NullLogger<OperatorMailer>.Instance);
        services.AddSingleton(Options.Create(new EmailOptions
        {
            Host = RelayHost,
            From = RelayFrom,
            PublicBaseUrl = RelayBaseUrl,
        }));
        services.AddSingleton(relay);
        services.AddSingleton<OperatorMailer>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// One meeting tomorrow with one proposal against it for the day after, so the suggested hour
    /// and the meeting's own are distinguishable in the operator's mail. Expiry and state are the
    /// parameters because they are what the sweep's predicate reads about a row nobody has been
    /// told about yet. Called twice it writes a second meeting on the same link, which is the
    /// batch the per-row save exists for.
    /// </summary>
    private static Guid SeedProposal(
        TestDatabase database,
        int expiresInSeconds,
        ProposalState state = ProposalState.Open)
    {
        using var context = database.CreateContext();
        var now = Now.ToUnixTimeSeconds();
        var link = context.Links.SingleOrDefault(row => row.Slug == Slug) ?? NewLink(context);

        // The next free hour, because Bookings carries a unique index on StartUtc filtered to
        // Confirmed — a second seeded meeting at the same instant is rejected by the schema.
        var booked = now + OneDaySeconds + (context.Bookings.Count() * OneHourSeconds);
        var booking = new Booking
        {
            Id = Guid.CreateVersion7(),
            LinkId = link.Id,
            StartUtc = booked,
            EndUtc = booked + MeetingSeconds,
            Status = BookingStatus.Confirmed,
            ManageTokenHash = Guid.CreateVersion7().ToString("N"),
            CreatedAt = now,
            Participants =
            [
                new BookingParticipant
                {
                    Id = Guid.CreateVersion7(),
                    Role = ParticipantRole.Booker,
                    Name = BookerName,
                    Email = BookerEmail,
                    TimeZoneId = BookerZone,
                },
            ],
        };
        context.Bookings.Add(booking);

        var proposed = booked + OneDaySeconds;
        var proposal = new BookingProposal
        {
            Id = Guid.CreateVersion7(),
            BookingId = booking.Id,
            StartUtc = proposed,
            EndUtc = proposed + MeetingSeconds,
            ExpiresAt = now + expiresInSeconds,
            CreatedAt = now,
            State = state,
            TokenHash = Guid.CreateVersion7().ToString("N"),
        };
        context.BookingProposals.Add(proposal);

        context.SaveChanges();
        return proposal.Id;
    }

    private static Link NewLink(KairosDbContext context)
    {
        var pattern = new Pattern { Id = Guid.CreateVersion7(), Name = PatternName };
        context.Patterns.Add(pattern);

        var link = new Link
        {
            Id = Guid.CreateVersion7(),
            Slug = Slug,
            Name = LinkName,
            PatternId = pattern.Id,
        };
        context.Links.Add(link);

        return link;
    }

    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    /// <summary>A relay that takes everything and says how much.</summary>
    private sealed class CountingRelay() : EmailSender(Options.Create(new EmailOptions()))
    {
        public int Sent { get; private set; }

        public override Task<string> SendAsync(MimeMessage message, CancellationToken cancellationToken)
        {
            Sent++;
            return Task.FromResult(RelayAccepted);
        }
    }

    /// <summary>
    /// Refuses its first message and takes every one after it, which is the shape of a relay
    /// having a bad moment rather than being down.
    /// </summary>
    private sealed class RefusingFirstRelay() : EmailSender(Options.Create(new EmailOptions()))
    {
        public int Attempts { get; private set; }

        public override Task<string> SendAsync(MimeMessage message, CancellationToken cancellationToken)
        {
            Attempts++;
            return Attempts == FirstAttempt
                ? throw new IOException(RelayRefused)
                : Task.FromResult(RelayAccepted);
        }
    }

    /// <summary>
    /// Takes everything, and reads the proposals table while it is doing it. Its own connection, so
    /// what it sees is what is committed at the moment of the send — the sweeper's change tracker
    /// would answer a different and less interesting question.
    /// </summary>
    private sealed class ObservingRelay(TestDatabase database)
        : EmailSender(Options.Create(new EmailOptions()))
    {
        public List<(ProposalState State, long? NotifiedAt)> Rows { get; } = [];

        public override Task<string> SendAsync(MimeMessage message, CancellationToken cancellationToken)
        {
            using var context = database.CreateContext();
            Rows.AddRange(context.BookingProposals.AsNoTracking()
                .Select(row => new { row.State, row.NotifiedAt })
                .AsEnumerable()
                .Select(row => (row.State, row.NotifiedAt)));

            return Task.FromResult(RelayAccepted);
        }
    }
}
