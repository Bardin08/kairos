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
/// Which proposals the sweeper closes, and how many times the operator hears about each.
/// </summary>
/// <remarks>
/// The work method is driven directly rather than through the timer. What the loop adds is a
/// cadence, and a test that started the service and waited for a sweep would be asserting on a
/// clock — the same test written twice a minute apart, one of which is flaky.
///
/// Nothing is substituted but the socket, which is <see cref="Integrations.BookingMailerTests"/>'s
/// rule: the sweeper resolves its own scope, its own context and its own mailer out of a container
/// built the way the application builds one.
/// </remarks>
public class ProposalSweeperTests
{
    private const int OneHourSeconds = 60 * 60;
    private const int OneDaySeconds = 24 * OneHourSeconds;
    private const int MeetingSeconds = 30 * 60;

    /// <summary>An expiry the clock has already passed, which is the whole trigger.</summary>
    private const int LapsedAnHourAgo = -OneHourSeconds;

    private const int LapsesTomorrow = OneDaySeconds;

    private const int NoNotices = 0;
    private const int OneNotice = 1;

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

    /// <summary>
    /// Twice through the sweeper, one message. <see cref="BookingProposal.NotifiedAt"/> is what
    /// makes the second tick a no-op, and without it a lapse the operator has already been told
    /// about is mailed again every minute for the life of the deployment.
    /// </summary>
    [Fact]
    public async Task Sweep_AProposalWhoseHoldHasLapsed_IsMarkedExpiredAndTheOperatorIsToldOnce()
    {
        using var database = new TestDatabase();
        await using var provider = NewHost(database);
        var relay = provider.GetRequiredService<CountingRelay>();
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

    [Fact]
    public async Task Sweep_AnOpenProposalStillInDate_IsLeftAlone()
    {
        using var database = new TestDatabase();
        await using var provider = NewHost(database);
        var relay = provider.GetRequiredService<CountingRelay>();
        var proposal = SeedProposal(database, LapsesTomorrow);

        await NewSweeper(provider).SweepAsync(TestContext.Current.CancellationToken);

        using var context = database.CreateContext();
        context.BookingProposals.Single(row => row.Id == proposal).State.ShouldBe(ProposalState.Open);
        relay.Sent.ShouldBe(NoNotices);
    }

    /// <summary>
    /// The state a tick that died between the save and the send leaves behind, and the reason the
    /// query asks about <see cref="BookingProposal.NotifiedAt"/> rather than about the state alone:
    /// the row is already Expired, nobody has heard, and the next tick is the only thing that will
    /// ever fix that.
    /// </summary>
    [Fact]
    public async Task Sweep_AnExpiredProposalNobodyWasToldAbout_IsPickedUpByTheNextTick()
    {
        using var database = new TestDatabase();
        await using var provider = NewHost(database);
        var relay = provider.GetRequiredService<CountingRelay>();
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
        await using var provider = NewHost(database);
        var relay = provider.GetRequiredService<CountingRelay>();
        var proposal = SeedProposal(database, LapsedAnHourAgo, ProposalState.Declined);

        await NewSweeper(provider).SweepAsync(TestContext.Current.CancellationToken);

        using var context = database.CreateContext();
        context.BookingProposals.Single(row => row.Id == proposal).State.ShouldBe(ProposalState.Declined);
        relay.Sent.ShouldBe(NoNotices);
    }

    private static ProposalSweeper NewSweeper(ServiceProvider provider) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            SystemClock.Instance,
            NullLogger<ProposalSweeper>.Instance);

    /// <summary>
    /// The container the sweeper resolves through, built the way the application builds one:
    /// options constructed by hand and registered as a singleton, so the process keeps exactly one
    /// EF model. <see cref="Integrations.MeetingEndedPollerTests"/> records what AddDbContext costs
    /// here.
    /// </summary>
    private static ServiceProvider NewHost(TestDatabase database)
    {
        var services = new ServiceCollection();
        services.AddSingleton(
            new DbContextOptionsBuilder<KairosDbContext>().UseSqlite(database.ConnectionString).Options);
        services.AddScoped<KairosDbContext>();
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton<ILogger<OperatorMailer>>(NullLogger<OperatorMailer>.Instance);
        services.AddSingleton(Options.Create(new EmailOptions
        {
            Host = RelayHost,
            From = RelayFrom,
            PublicBaseUrl = RelayBaseUrl,
        }));

        // Registered twice and resolved once, so the test holds the same instance the mailer sends
        // through — the shape Program.cs uses for the Google adapter and for the same reason.
        services.AddSingleton<CountingRelay>();
        services.AddSingleton<EmailSender>(provider => provider.GetRequiredService<CountingRelay>());
        services.AddSingleton<OperatorMailer>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// One meeting tomorrow with one proposal against it for the day after, so the suggested hour
    /// and the meeting's own are distinguishable in the operator's mail. Expiry and state are the
    /// parameters because they are what the sweep's predicate reads about a row nobody has been
    /// told about yet.
    /// </summary>
    private static Guid SeedProposal(
        TestDatabase database,
        int expiresInSeconds,
        ProposalState state = ProposalState.Open)
    {
        using var context = database.CreateContext();
        var now = SystemClock.Instance.GetCurrentInstant().ToUnixTimeSeconds();

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

        var booked = now + OneDaySeconds;
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

    /// <summary>
    /// A relay that takes everything and says how much. Substituted at the socket rather than
    /// mocked at the protocol, exactly as the proposal endpoints' own relays are.
    /// </summary>
    private sealed class CountingRelay() : EmailSender(Options.Create(new EmailOptions()))
    {
        public int Sent { get; private set; }

        public override Task<string> SendAsync(MimeMessage message, CancellationToken cancellationToken)
        {
            Sent++;
            return Task.FromResult(RelayAccepted);
        }
    }
}
