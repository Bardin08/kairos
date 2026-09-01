using Kairos.Api.Features.Bookings;
using Kairos.Api.Integrations.Email;
using Kairos.Api.Integrations.Webhooks;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Security;

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
/// Which reminders one tick sends, which it refuses to, and what a row is left holding afterwards.
/// </summary>
/// <remarks>
/// Shaped after <see cref="ProposalSweeperTests"/>, whose fixtures this reuses — the same
/// file-backed <see cref="TestDatabase"/>, the same <see cref="FixedClock"/>, the same relay
/// substituted at the socket with everything above it real. The work method is driven directly
/// rather than through the timer: a test that waited for a tick would be asserting on a clock, and
/// every case here is a statement about which side of a boundary an instant falls on, which cannot
/// be said at all while the clock moves underneath it.
///
/// The doubles are declared here rather than shared with that class. Two suites reaching into one
/// another's private doubles is the coupling rule 3 exists to prevent, and the shapes differ:
/// these count messages *and* keep them, because the manage link the sweeper had to decrypt is
/// only observable in the message it produced.
/// </remarks>
public class ReminderSweeperTests
{
    private const int MeetingMinutes = 30;
    private const int NoMargin = 0;

    /// <summary>How long before the sweep the restarted case's booking was made.</summary>
    private const int HoursBeforeTheRestart = 2;

    private const string PatternName = "Weekdays";
    private const string Slug = "consulting";
    private const string LinkName = "Consulting call";
    private const string BookerName = "Ada Lovelace";
    private const string BookerEmail = "ada@example.com";
    private const string BookerZone = "Europe/Warsaw";

    /// <summary>The plaintext behind <see cref="Reminder.Secret"/>, per FollowUpWorkerTests.</summary>
    private const string ManageToken = "8f14e45fceea167a5a36dedd4bea2543";

    private const string JoinUrl = "https://meet.example.com/abc-defg-hij";
    private const string ConferenceId = "abc-defg-hij";

    private const string RelayHost = "relay.example.com";
    private const string RelayFrom = "Kairos <notify@example.com>";
    private const string RelayBaseUrl = "https://kairos.example.com";

    /// <summary>What a relay answers a message it took. Never read; it only has to exist.</summary>
    private const string RelayAccepted = "250 2.0.0 OK";

    private const string RelayRefused = "The relay dropped the connection.";

    private const int NoReminders = 0;

    /// <summary>Nothing was tried, which is what an unconfigured relay leaves behind.</summary>
    private const int NoAttempts = 0;
    private const int OneReminder = 1;

    /// <summary>The first send, and the retry that follows it.</summary>
    private const int TwoAttempts = 2;

    private const int OneAttempt = 1;

    /// <summary>One more due reminder than <see cref="ReminderSweeper.MaxRemindersPerTick"/> takes.</summary>
    private const int OneOverTheCap = 1;

    /// <summary>
    /// The instant every start here is placed relative to, and the one the sweeper reads. Fixed,
    /// because "due but not started" is a window a test can only construct against a named clock.
    /// </summary>
    private static readonly Instant Now = Instant.FromUtc(2026, 9, 7, 9, 0);

    /// <summary>The link every seeded booking hangs off, so its identity can be named once.</summary>
    private static readonly Guid LinkId = Guid.CreateVersion7();

    /// <summary>
    /// How far each start is placed from the boundary it is about. Any distance short of
    /// <see cref="ReminderSchedule.Lead"/> would do; what matters is the side.
    /// </summary>
    private static readonly Duration AnHour = Duration.FromHours(1);

    private static readonly Duration Meeting = Duration.FromMinutes(MeetingMinutes);

    /// <summary>Its due moment passed an hour ago and it is a day out. The ordinary send.</summary>
    private static readonly Instant JustDue = Now + ReminderSchedule.Lead - AnHour;

    /// <summary>Its due moment is an hour away, so this tick is not the one.</summary>
    private static readonly Instant NotYetDue = Now + ReminderSchedule.Lead + AnHour;

    /// <summary>
    /// Due nearly a whole lead ago and still ahead: the process was down over the due moment, and
    /// a reminder about a meeting an hour away is late and useful rather than wrong.
    /// </summary>
    private static readonly Instant SleptThrough = Now + AnHour;

    /// <summary>Under way. Whatever its row says, a reminder about it is noise.</summary>
    private static readonly Instant AlreadyStarted = Now - AnHour;

    /// <summary>
    /// When the restarted case's booking was made — before its own due moment, which is what makes
    /// <see cref="ReminderSchedule.ShouldSchedule"/> write a row at all, and before the restart.
    /// </summary>
    private static readonly Instant Booked = Now - Duration.FromHours(HoursBeforeTheRestart);

    /// <summary>
    /// Thirty-two zero bytes, base64. A key a test can name and no deployment would hold —
    /// <see cref="SecretProtector"/> only checks the length.
    /// </summary>
    private static readonly string TestKey = Convert.ToBase64String(new byte[SecretProtector.KeyBytes]);

    [Fact]
    public async Task Sweep_ADueReminder_SendsItAndStampsSentAt()
    {
        using var database = new TestDatabase();
        var relay = new CollectingRelay();
        await using var provider = NewHost(database, relay);
        var reminder = SeedReminder(database, JustDue);

        await NewSweeper(provider).SweepAsync(TestContext.Current.CancellationToken);

        relay.Sent.Count.ShouldBe(OneReminder);

        using var context = database.CreateContext();
        var swept = context.Reminders.AsNoTracking().Single(row => row.Id == reminder);
        swept.SentAt.ShouldBe(Now.ToUnixTimeSeconds());
        swept.Attempts.ShouldBe(OneAttempt);
        swept.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task Sweep_AReminderNotYetDue_SendsNothing()
    {
        using var database = new TestDatabase();
        var relay = new CollectingRelay();
        await using var provider = NewHost(database, relay);
        var reminder = SeedReminder(database, NotYetDue);

        await NewSweeper(provider).SweepAsync(TestContext.Current.CancellationToken);

        relay.Sent.Count.ShouldBe(NoReminders);

        using var context = database.CreateContext();
        context.Reminders.AsNoTracking().Single(row => row.Id == reminder).SentAt.ShouldBeNull();
    }

    [Fact]
    public async Task Sweep_ARowAlreadySent_IsNotSentAgain()
    {
        using var database = new TestDatabase();
        var relay = new CollectingRelay();
        await using var provider = NewHost(database, relay);
        var reminder = SeedReminder(database, JustDue);

        var sweeper = NewSweeper(provider);
        await sweeper.SweepAsync(TestContext.Current.CancellationToken);
        await sweeper.SweepAsync(TestContext.Current.CancellationToken);

        relay.Sent.Count.ShouldBe(OneReminder);

        using var context = database.CreateContext();
        context.Reminders.AsNoTracking().Single(row => row.Id == reminder).Attempts.ShouldBe(OneAttempt);
    }

    /// <summary>A cancelled meeting whose row somehow survived. Belt and braces over Task 5.</summary>
    [Fact]
    public async Task Sweep_ABookingNoLongerConfirmed_SendsNothing()
    {
        using var database = new TestDatabase();
        var relay = new CollectingRelay();
        await using var provider = NewHost(database, relay);
        SeedReminder(database, JustDue, BookingStatus.Cancelled);

        await NewSweeper(provider).SweepAsync(TestContext.Current.CancellationToken);

        relay.Sent.Count.ShouldBe(NoReminders);
    }

    /// <summary>
    /// The lateness rule, and the whole of it. A deploy at 13:00 still sends the reminder for
    /// tomorrow's 14:00 — late and useful. A reminder for a meeting already under way is noise.
    /// </summary>
    [Fact]
    public async Task Sweep_ADueMomentTheProcessSleptThrough_StillSendsIfTheMeetingHasNotStarted()
    {
        using var database = new TestDatabase();
        var relay = new CollectingRelay();
        await using var provider = NewHost(database, relay);
        var reminder = SeedReminder(database, SleptThrough);

        await NewSweeper(provider).SweepAsync(TestContext.Current.CancellationToken);

        relay.Sent.Count.ShouldBe(OneReminder);

        using var context = database.CreateContext();
        context.Reminders.AsNoTracking().Single(row => row.Id == reminder).SentAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Sweep_AMeetingAlreadyStarted_SendsNothing()
    {
        using var database = new TestDatabase();
        var relay = new CollectingRelay();
        await using var provider = NewHost(database, relay);
        var reminder = SeedReminder(database, AlreadyStarted);

        await NewSweeper(provider).SweepAsync(TestContext.Current.CancellationToken);

        relay.Sent.Count.ShouldBe(NoReminders);

        using var context = database.CreateContext();
        context.Reminders.AsNoTracking().Single(row => row.Id == reminder).SentAt.ShouldBeNull();
    }

    /// <summary>
    /// The roadmap's own clause. The state is the row, so a host that never saw the booking
    /// written still sends its reminder — which is what "durable" means here and what an
    /// in-memory timer could not do.
    /// </summary>
    /// <remarks>
    /// The booking is written by <see cref="BookingWriter"/> through the first host rather than
    /// seeded as rows, so what the second host finds is the row the application actually writes,
    /// scheduled by the writer's own rule. <see cref="TestDatabase"/> is file-backed, which is what
    /// makes disposing the first container a restart rather than a re-resolved service.
    /// </remarks>
    [Fact]
    public async Task Sweep_AfterTheProcessRestarted_StillSendsTheReminder()
    {
        using var database = new TestDatabase();
        Guid scheduled;

        // Migrations and the link first, through the fixture: a context resolved from a container
        // applies no schema, which is the one thing TestDatabase.CreateContext does that
        // AddDbContext does not.
        using (var schema = database.CreateContext())
        {
            SeedLink(schema);
        }

        await using (var first = NewHost(database, new CollectingRelay()))
        {
            using var scope = first.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<KairosDbContext>();

            var clock = new FixedClock(Booked);
            var written = new BookingWriter(
                    context,
                    clock,
                    new OutboxWriter(context, clock),
                    new FollowUpWriter(context, new SecretProtector(TestKey), clock))
                .TryCreate(
                    LinkId,
                    Slug,
                    JustDue,
                    Meeting,
                    new BookerDetails(BookerName, BookerEmail, BookerZone),
                    string.Empty,
                    NoMargin);

            written.Success.ShouldBeTrue();
            scheduled = context.Reminders.AsNoTracking().Single().Id;
        }

        var relay = new CollectingRelay();
        await using var second = NewHost(database, relay);

        await NewSweeper(second).SweepAsync(TestContext.Current.CancellationToken);

        relay.Sent.Count.ShouldBe(OneReminder);

        using var verify = database.CreateContext();
        verify.Reminders.AsNoTracking().Single(row => row.Id == scheduled).SentAt.ShouldNotBeNull();
    }

    /// <summary>
    /// A relay that refuses leaves the row unsent and countable, so the next tick tries again and
    /// somebody can see why it did not go.
    /// </summary>
    [Fact]
    public async Task Sweep_ARelayThatRefuses_LeavesTheRowUnsentAndRecordsWhy()
    {
        using var database = new TestDatabase();
        var relay = new ToggleableRelay { Refuse = true };
        await using var provider = NewHost(database, relay);
        var reminder = SeedReminder(database, JustDue);
        var sweeper = NewSweeper(provider);

        await sweeper.SweepAsync(TestContext.Current.CancellationToken);

        using (var afterRefusal = database.CreateContext())
        {
            var stored = afterRefusal.Reminders.AsNoTracking().Single(row => row.Id == reminder);
            stored.SentAt.ShouldBeNull();
            stored.Attempts.ShouldBe(OneAttempt);
            stored.LastError.ShouldNotBeNullOrWhiteSpace();

            // The secret is what the retry will need, so a refusal must not spend it.
            stored.Secret.ShouldNotBeNull();
        }

        relay.Refuse = false;
        await sweeper.SweepAsync(TestContext.Current.CancellationToken);

        using var afterRetry = database.CreateContext();
        var sent = afterRetry.Reminders.AsNoTracking().Single(row => row.Id == reminder);
        sent.SentAt.ShouldNotBeNull();
        sent.Attempts.ShouldBe(TwoAttempts);
        relay.Attempts.ShouldBe(TwoAttempts);
    }

    /// <summary>
    /// The manage token is ciphertext on the row and plaintext in the link, so a sweeper that
    /// never decrypted it would send a message the invitee cannot act on — and every other case
    /// here would still pass, because they count messages rather than read them.
    /// </summary>
    [Fact]
    public async Task Sweep_ARowCarryingASecret_PutsTheManageLinkInTheMessage()
    {
        using var database = new TestDatabase();
        var relay = new CollectingRelay();
        await using var provider = NewHost(database, relay);
        SeedReminder(database, JustDue, joinUrl: JoinUrl);

        await NewSweeper(provider).SweepAsync(TestContext.Current.CancellationToken);

        var text = relay.Sent.ShouldHaveSingleItem().TextBody.ShouldNotBeNull();
        text.ShouldContain($"{RelayBaseUrl}/manage/{ManageToken}");
        text.ShouldContain(JoinUrl);
    }

    /// <summary>
    /// The ciphertext must not outlive its use — <see cref="FollowUpWorker"/>'s rule, and rule 15's
    /// reasoning. A reminder row survives its send, so nulling this is the only thing that stops
    /// every sent reminder holding a live manage token for the life of the booking.
    /// </summary>
    [Fact]
    public async Task Sweep_AReminderItSent_DropsTheSecretNothingWillSpendAgain()
    {
        using var database = new TestDatabase();
        var relay = new CollectingRelay();
        await using var provider = NewHost(database, relay);
        var reminder = SeedReminder(database, JustDue);

        await NewSweeper(provider).SweepAsync(TestContext.Current.CancellationToken);

        using var context = database.CreateContext();
        context.Reminders.AsNoTracking().Single(row => row.Id == reminder).Secret.ShouldBeNull();
    }

    /// <summary>
    /// The other end of that rule. A reminder whose meeting has begun will never be selected
    /// again, so it is given up on — and giving up drops the secret for the same reason sending
    /// does, or a relay that was down over one lead leaves a live token in the database for ever.
    /// </summary>
    [Fact]
    public async Task Sweep_AReminderItWillNeverSend_DropsTheSecretOnItsWayPast()
    {
        using var database = new TestDatabase();
        var relay = new CollectingRelay();
        await using var provider = NewHost(database, relay);
        var reminder = SeedReminder(database, AlreadyStarted);

        await NewSweeper(provider).SweepAsync(TestContext.Current.CancellationToken);

        using var context = database.CreateContext();
        var stranded = context.Reminders.AsNoTracking().Single(row => row.Id == reminder);
        stranded.Secret.ShouldBeNull();

        // The row itself stays, so an operator can still find out that nobody was reminded.
        stranded.SentAt.ShouldBeNull();
    }

    /// <summary>
    /// A booking written before <see cref="Reminder.Secret"/> existed, and a move from inside the
    /// lead to outside it, both leave a row with no token to build a manage link from. The
    /// reminder still goes; only its secondary link is missing.
    /// </summary>
    [Fact]
    public async Task Sweep_ARowWithNoSecret_StillSendsTheReminder()
    {
        using var database = new TestDatabase();
        var relay = new CollectingRelay();
        await using var provider = NewHost(database, relay);
        var reminder = SeedReminder(database, JustDue, manageToken: null);

        await NewSweeper(provider).SweepAsync(TestContext.Current.CancellationToken);

        relay.Sent.Count.ShouldBe(OneReminder);

        using var context = database.CreateContext();
        context.Reminders.AsNoTracking().Single(row => row.Id == reminder).SentAt.ShouldNotBeNull();
    }

    /// <summary>
    /// No relay configured. The reminder is held, not spent: nothing is stamped, nothing is
    /// counted, the secret stays, and the whole of it goes out on the first tick after somebody
    /// fixes the configuration.
    /// </summary>
    /// <remarks>
    /// The case this is really about is not the fresh install where nobody is worse off. It is one
    /// deploy with a missing Email__Host — a secret that failed to mount, a typo in a values file —
    /// which <see cref="IOptions{TOptions}"/> holds for the life of the process. A sweeper that
    /// marked those rows sent would record a delivery that never happened, on the one column whose
    /// docstring promises the opposite, and destroy the token each held. Hence the second half:
    /// what makes this path acceptable is that it is recoverable, and the manage link surviving
    /// into the message is how that is visible rather than assumed.
    /// </remarks>
    [Fact]
    public async Task Sweep_NoRelayConfigured_HoldsTheReminderUntilThereIsOne()
    {
        using var database = new TestDatabase();
        var reminder = SeedReminder(database, JustDue);

        var unreachable = new CollectingRelay();
        await using (var unconfigured = NewHost(database, unreachable, new EmailOptions()))
        {
            await NewSweeper(unconfigured).SweepAsync(TestContext.Current.CancellationToken);
        }

        unreachable.Sent.Count.ShouldBe(NoReminders);

        using (var held = database.CreateContext())
        {
            var stored = held.Reminders.AsNoTracking().Single(row => row.Id == reminder);
            stored.SentAt.ShouldBeNull();
            stored.Attempts.ShouldBe(NoAttempts);
            stored.Secret.ShouldNotBeNull();
            new SecretProtector(TestKey).Unprotect(stored.Secret).ShouldBe(ManageToken);
        }

        // The configuration is fixed and the process comes back with a relay behind it.
        var relay = new CollectingRelay();
        await using var configured = NewHost(database, relay);

        await NewSweeper(configured).SweepAsync(TestContext.Current.CancellationToken);

        var text = relay.Sent.ShouldHaveSingleItem().TextBody.ShouldNotBeNull();
        text.ShouldContain($"{RelayBaseUrl}/manage/{ManageToken}");

        using var sent = database.CreateContext();
        sent.Reminders.AsNoTracking().Single(row => row.Id == reminder).SentAt.ShouldNotBeNull();
    }

    /// <summary>
    /// The bound on a dead relay. A send to one that never answers costs the whole of
    /// <see cref="EmailSender.SendTimeout"/>, so a tick attempts only as many as fit inside the
    /// interval that schedules it. Oldest due first, so the cap delays rather than starves: the
    /// row left behind is the one that came due most recently, and the next tick takes it.
    /// </summary>
    [Fact]
    public async Task Sweep_MoreDueRemindersThanOneTickWillAttempt_TakesTheOldestAndLeavesTheRest()
    {
        using var database = new TestDatabase();
        var relay = new CollectingRelay();
        await using var provider = NewHost(database, relay);

        // Staggered towards the clock, so "came due first" is a fact about the rows rather than
        // about insertion order, and every start is distinct — Bookings carries a unique index on
        // StartUtc filtered to Confirmed.
        var scheduled = Enumerable.Range(0, ReminderSweeper.MaxRemindersPerTick + OneOverTheCap)
            .Select(index => SeedReminder(database, JustDue - (AnHour * index)))
            .ToList();

        await NewSweeper(provider).SweepAsync(TestContext.Current.CancellationToken);

        relay.Sent.Count.ShouldBe(ReminderSweeper.MaxRemindersPerTick);

        // The first seeded is the one furthest from due, so it is the one the cap leaves behind.
        var newest = scheduled[0];

        using var context = database.CreateContext();
        context.Reminders.AsNoTracking().Single(row => row.Id == newest).SentAt.ShouldBeNull();
    }

    private static ReminderSweeper NewSweeper(ServiceProvider provider) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(Now),
            provider.GetRequiredService<IOptions<EmailOptions>>(),
            NullLogger<ReminderSweeper>.Instance);

    /// <summary>
    /// The container the sweeper resolves through, built the way the application builds one:
    /// options constructed by hand and registered as a singleton, so the process keeps exactly one
    /// EF model — <see cref="ProposalSweeperTests"/> and
    /// <see cref="Integrations.MeetingEndedPollerTests"/> record what AddDbContext costs here.
    /// </summary>
    private static ServiceProvider NewHost(
        TestDatabase database, EmailSender relay, EmailOptions? email = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(
            new DbContextOptionsBuilder<KairosDbContext>().UseSqlite(database.ConnectionString).Options);
        services.AddScoped<KairosDbContext>();
        services.AddSingleton<IClock>(new FixedClock(Now));
        services.AddLogging();
        services.AddSingleton<ILogger<BookingMailer>>(NullLogger<BookingMailer>.Instance);
        services.AddSingleton(Options.Create(email ?? new EmailOptions
        {
            Host = RelayHost,
            From = RelayFrom,
            PublicBaseUrl = RelayBaseUrl,
        }));
        services.AddSingleton(relay);
        services.AddSingleton<MailHtmlRenderer>();
        services.AddSingleton<IcsBuilder>();
        services.AddSingleton<BookingMailer>();
        services.AddSingleton(new SecretProtector(TestKey));

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// One meeting on the shared link with the reminder its start earns, returning the reminder's
    /// identity. The start is the parameter because every case here is about where it falls
    /// relative to the clock and the lead; the status, the token and the conference are the three
    /// other things the sweep's predicate or its message reads.
    /// </summary>
    private static Guid SeedReminder(
        TestDatabase database,
        Instant start,
        BookingStatus status = BookingStatus.Confirmed,
        string? manageToken = ManageToken,
        string? joinUrl = null)
    {
        using var context = database.CreateContext();
        SeedLink(context);

        var bookingId = Guid.CreateVersion7();
        context.Bookings.Add(new Booking
        {
            Id = bookingId,
            LinkId = LinkId,
            StartUtc = start.ToUnixTimeSeconds(),
            EndUtc = (start + Meeting).ToUnixTimeSeconds(),
            Status = status,
            ManageTokenHash = Guid.CreateVersion7().ToString("N"),
            CreatedAt = Now.ToUnixTimeSeconds(),
            Participants =
            [
                new BookingParticipant
                {
                    Id = Guid.CreateVersion7(),
                    BookingId = bookingId,
                    Role = ParticipantRole.Booker,
                    Name = BookerName,
                    Email = BookerEmail,
                    TimeZoneId = BookerZone,
                },
            ],
        });

        if (joinUrl is not null)
        {
            context.ConferenceLinks.Add(new ConferenceLink
            {
                BookingId = bookingId,
                Provider = IntegrationProvider.Google,
                ExternalId = ConferenceId,
                JoinUrl = joinUrl,
                CreatedAt = Now.ToUnixTimeSeconds(),
            });
        }

        var reminder = new Reminder
        {
            BookingId = bookingId,
            DueAt = ReminderSchedule.DueAt(start).ToUnixTimeSeconds(),
            Secret = manageToken is null ? null : new SecretProtector(TestKey).Protect(manageToken),
        };
        context.Reminders.Add(reminder);

        context.SaveChanges();
        return reminder.Id;
    }

    /// <summary>Idempotent, because a case that seeds two meetings seeds them on one link.</summary>
    private static void SeedLink(KairosDbContext context)
    {
        if (context.Links.Any(row => row.Id == LinkId))
        {
            return;
        }

        var pattern = new Pattern { Id = Guid.CreateVersion7(), Name = PatternName };
        context.Patterns.Add(pattern);
        context.Links.Add(new Link
        {
            Id = LinkId,
            Slug = Slug,
            Name = LinkName,
            PatternId = pattern.Id,
            MarginMinutes = NoMargin,
        });
        context.SaveChanges();
    }

    /// <summary>Takes everything, and keeps it — the message is where the manage link shows up.</summary>
    private sealed class CollectingRelay() : EmailSender(Options.Create(new EmailOptions()))
    {
        public List<MimeMessage> Sent { get; } = [];

        public override Task<string> SendAsync(MimeMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.FromResult(RelayAccepted);
        }
    }

    /// <summary>Refuses or takes everything, on command, counting every attempt either way.</summary>
    private sealed class ToggleableRelay() : EmailSender(Options.Create(new EmailOptions()))
    {
        public bool Refuse { get; set; }

        public int Attempts { get; private set; }

        public override Task<string> SendAsync(MimeMessage message, CancellationToken cancellationToken)
        {
            Attempts++;
            return Refuse
                ? Task.FromException<string>(new IOException(RelayRefused))
                : Task.FromResult(RelayAccepted);
        }
    }
}
