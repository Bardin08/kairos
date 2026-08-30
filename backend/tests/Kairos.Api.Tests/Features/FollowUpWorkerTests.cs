using Kairos.Api.Features.Bookings;
using Kairos.Api.Integrations;
using Kairos.Api.Integrations.Email;
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
/// One pass of the queue: which third parties it reaches, in what order, and what a row is left
/// holding when one of them refuses.
/// </summary>
/// <remarks>
/// The two providers are the fake and the stub <see cref="Integrations.CalendarSyncTests"/>
/// defines — copied rather than shared, because that class is under review at the same time as
/// this one and a shared home for them is a design decision for someone not mid-review to make.
/// </remarks>
public class FollowUpWorkerTests
{
    private const string PatternName = "Weekdays";
    private const string Slug = "consulting";
    private const string LinkName = "Consulting call";
    private const string BookerName = "Ada Lovelace";
    private const string BookerEmail = "ada@example.com";
    private const string BookerZone = "Europe/Warsaw";
    private const long MeetingStartUtc = 1_788_000_000;
    private const long MeetingEndUtc = 1_788_003_600;
    private const long CreatedAtUtc = 1_788_000_000;

    private const string ManageToken = "8f14e45fceea167a5a36dedd4bea2543";

    private const string RelayHost = "relay.example.com";
    private const string RelayFrom = "Kairos <notify@example.com>";
    private const string RelayBaseUrl = "https://kairos.example.com";
    private const string RelayAccepted = "250 2.0.0 OK";
    private const string RelayRefused = "The relay refused the message.";

    private const string EventId = "e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0";
    private const string CalendarUid = "e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0@google.com";
    private const string ConferenceId = "abc-defg-hij";
    private const string JoinUrl = "https://meet.example.com/abc-defg-hij";

    private const string CalendarStep = "calendar";
    private const string MailStep = "mail";

    private const int ReachedOnce = 1;
    private const int OneAttempt = 1;

    /// <summary>Thirty-two zero bytes, base64 — a key a test can name, per FollowUpWriterTests.</summary>
    private static readonly string TestKey = Convert.ToBase64String(new byte[SecretProtector.KeyBytes]);

    private static readonly Instant Now = Instant.FromUnixTimeSeconds(CreatedAtUtc);

    [Fact]
    public async Task DrainAsync_ACreatedFollowUp_SyncsThenMailsThenDeletesTheRow()
    {
        using var database = new TestDatabase();
        using var seed = database.CreateContext();
        var booking = SeedBooking(seed);
        SeedFollowUp(seed, booking.Id, FollowUpType.Created, ManageToken);

        var calendar = new FakeCalendar();
        var relay = new CountingRelay();
        using var provider = NewHost(database, relay, calendar, new StubConference());

        await NewWorker(provider).DrainAsync(TestContext.Current.CancellationToken);

        using var verify = database.CreateContext();
        verify.FollowUps.AsNoTracking().ShouldBeEmpty();
        relay.Sent.ShouldBe(ReachedOnce);
        verify.CalendarEntries.AsNoTracking().Single().State.ShouldBe(CalendarSyncState.Synced);
    }

    [Fact]
    public async Task DrainAsync_ACreatedFollowUp_ReachesTheCalendarBeforeTheRelay()
    {
        using var database = new TestDatabase();
        using var seed = database.CreateContext();
        var booking = SeedBooking(seed);
        SeedFollowUp(seed, booking.Id, FollowUpType.Created, ManageToken);

        List<string> order = [];
        using var provider = NewHost(
            database, new OrderingRelay(order), new OrderingCalendar(order), new StubConference());

        await NewWorker(provider).DrainAsync(TestContext.Current.CancellationToken);

        order.ShouldBe([CalendarStep, MailStep]);
    }

    [Fact]
    public async Task DrainAsync_TheRelayRefuses_KeepsTheRowWithItsSecretAndRecordsWhy()
    {
        using var database = new TestDatabase();
        using var seed = database.CreateContext();
        var booking = SeedBooking(seed);
        var followUp = SeedFollowUp(seed, booking.Id, FollowUpType.Created, ManageToken);

        var relay = new ToggleableRelay { Refuse = true };
        using var provider = NewHost(database, relay, new FakeCalendar(), new StubConference());

        await NewWorker(provider).DrainAsync(TestContext.Current.CancellationToken);

        using var verify = database.CreateContext();
        var stored = verify.FollowUps.AsNoTracking().Single();
        stored.Attempts.ShouldBe(OneAttempt);
        stored.Secret.ShouldNotBeNull();
        new SecretProtector(TestKey).Unprotect(stored.Secret).ShouldBe(ManageToken);
        stored.LastError.ShouldBe(
            $"The relay would not take the notice for follow-up {followUp.Id}.");
    }

    [Fact]
    public async Task DrainAsync_RetriedAfterAMailFailure_LeavesTheCalendarAloneAndSynced()
    {
        using var database = new TestDatabase();
        using var seed = database.CreateContext();
        var booking = SeedBooking(seed);
        SeedFollowUp(seed, booking.Id, FollowUpType.Created, ManageToken);

        var calendar = new FakeCalendar();
        var relay = new ToggleableRelay { Refuse = true };
        using var provider = NewHost(database, relay, calendar, new StubConference());
        var worker = NewWorker(provider);

        await worker.DrainAsync(TestContext.Current.CancellationToken);
        relay.Refuse = false;
        await worker.DrainAsync(TestContext.Current.CancellationToken);

        using var verify = database.CreateContext();
        verify.FollowUps.AsNoTracking().ShouldBeEmpty();
        calendar.Creates.ShouldBe(ReachedOnce);
        verify.CalendarEntries.AsNoTracking().Single().State.ShouldBe(CalendarSyncState.Synced);
    }

    [Fact]
    public async Task DrainAsync_TheLastAttemptFails_NullsTheSecretAndStopsRetrying()
    {
        using var database = new TestDatabase();
        using var seed = database.CreateContext();
        var booking = SeedBooking(seed);
        SeedFollowUp(seed, booking.Id, FollowUpType.Created, ManageToken);

        var relay = new ToggleableRelay { Refuse = true };
        using var provider = NewHost(database, relay, new FakeCalendar(), new StubConference());
        var worker = NewWorker(provider);

        for (var attempt = 0; attempt < FollowUpWorker.MaxAttempts; attempt++)
        {
            await worker.DrainAsync(TestContext.Current.CancellationToken);
        }

        using (var verify = database.CreateContext())
        {
            var stored = verify.FollowUps.AsNoTracking().Single();
            stored.Attempts.ShouldBe(FollowUpWorker.MaxAttempts);
            stored.Secret.ShouldBeNull();
        }

        var attemptsSoFar = relay.Attempts;
        await worker.DrainAsync(TestContext.Current.CancellationToken);

        relay.Attempts.ShouldBe(attemptsSoFar);
    }

    [Fact]
    public async Task DrainAsync_ACancelledFollowUp_WithdrawsTheEventThenMails()
    {
        using var database = new TestDatabase();
        using var seed = database.CreateContext();
        var booking = SeedBooking(seed);
        booking.Status = BookingStatus.Cancelled;
        seed.CalendarEntries.Add(new CalendarEntry
        {
            BookingId = booking.Id,
            Provider = IntegrationProvider.Google,
            ExternalEventId = EventId,
            CalendarUid = CalendarUid,
            State = CalendarSyncState.Synced,
            UpdatedAt = CreatedAtUtc,
        });
        seed.SaveChanges();
        SeedFollowUp(seed, booking.Id, FollowUpType.Cancelled, manageToken: null);

        List<string> order = [];
        using var provider = NewHost(
            database, new OrderingRelay(order), new OrderingCalendar(order), new StubConference());

        await NewWorker(provider).DrainAsync(TestContext.Current.CancellationToken);

        order.ShouldBe([CalendarStep, MailStep]);

        using var verify = database.CreateContext();
        verify.FollowUps.AsNoTracking().ShouldBeEmpty();
    }

    private static ServiceProvider NewHost(
        TestDatabase database,
        EmailSender relay,
        ICalendarWriter calendar,
        IConferenceCreator conference)
    {
        var services = new ServiceCollection();
        services.AddSingleton(
            new DbContextOptionsBuilder<KairosDbContext>().UseSqlite(database.ConnectionString).Options);
        services.AddScoped<KairosDbContext>();
        services.AddSingleton<IClock>(new FixedClock(Now));
        services.AddLogging();
        services.AddSingleton<ILogger<BookingMailer>>(NullLogger<BookingMailer>.Instance);
        services.AddSingleton<ILogger<CalendarSyncService>>(NullLogger<CalendarSyncService>.Instance);
        services.AddSingleton(Options.Create(new EmailOptions
        {
            Host = RelayHost,
            From = RelayFrom,
            PublicBaseUrl = RelayBaseUrl,
        }));
        services.AddSingleton(relay);
        services.AddSingleton<MailHtmlRenderer>();
        services.AddSingleton<IcsBuilder>();
        services.AddSingleton<BookingMailer>();
        services.AddSingleton(calendar);
        services.AddSingleton(conference);
        services.AddScoped<CalendarSyncService>();
        services.AddScoped<IntegrationCredentialStore>();
        services.AddScoped<MeetingIdentityResolver>();
        services.AddSingleton(new SecretProtector(TestKey));

        return services.BuildServiceProvider();
    }

    private static FollowUpWorker NewWorker(ServiceProvider provider) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FollowUpSignal(),
            NullLogger<FollowUpWorker>.Instance);

    /// <summary>The shape <see cref="Integrations.CalendarSyncTests"/> seeds, one confirmed meeting.</summary>
    private static Booking SeedBooking(KairosDbContext context)
    {
        var pattern = new Pattern { Id = Guid.CreateVersion7(), Name = PatternName };
        var link = new Link
        {
            Id = Guid.CreateVersion7(),
            Slug = Slug,
            Name = LinkName,
            PatternId = pattern.Id,
        };

        var bookingId = Guid.CreateVersion7();
        var booking = new Booking
        {
            Id = bookingId,
            LinkId = link.Id,
            StartUtc = MeetingStartUtc,
            EndUtc = MeetingEndUtc,
            Status = BookingStatus.Confirmed,
            ManageTokenHash = "hash",
            CreatedAt = MeetingStartUtc,
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
        };

        context.Patterns.Add(pattern);
        context.Links.Add(link);
        context.Bookings.Add(booking);
        context.SaveChanges();

        return booking;
    }

    private static FollowUp SeedFollowUp(
        KairosDbContext context, Guid bookingId, FollowUpType type, string? manageToken)
    {
        var followUp = new FollowUp
        {
            BookingId = bookingId,
            Type = type,
            Secret = manageToken is null ? null : new SecretProtector(TestKey).Protect(manageToken),
            CreatedAt = CreatedAtUtc,
        };
        context.FollowUps.Add(followUp);
        context.SaveChanges();
        return followUp;
    }

    /// <summary>
    /// A calendar, not a call recorder — <see cref="Integrations.CalendarSyncTests"/>'s own
    /// reasoning for the shape.
    /// </summary>
    private sealed class FakeCalendar : ICalendarWriter
    {
        public IntegrationProvider Provider => IntegrationProvider.Google;

        public int Creates { get; private set; }

        public Task<bool> IsConnectedAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<PlacedEvent> CreateEventAsync(
            CalendarEventRequest request, CancellationToken cancellationToken)
        {
            Creates++;
            return Task.FromResult(new PlacedEvent(EventId, CalendarUid));
        }

        public Task UpdateEventAsync(
            string externalEventId, CalendarEventRequest request, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task CancelEventAsync(string externalEventId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class StubConference : IConferenceCreator
    {
        public IntegrationProvider Provider => IntegrationProvider.Google;

        public Task<ConferenceDetails> CreateAsync(Guid bookingId, CancellationToken cancellationToken) =>
            Task.FromResult(new ConferenceDetails(ConferenceId, new Uri(JoinUrl)));
    }

    /// <summary>Takes everything and counts how many times.</summary>
    private sealed class CountingRelay() : EmailSender(Options.Create(new EmailOptions()))
    {
        public int Sent { get; private set; }

        public override Task<string> SendAsync(MimeMessage message, CancellationToken cancellationToken)
        {
            Sent++;
            return Task.FromResult(RelayAccepted);
        }
    }

    /// <summary>Refuses or takes everything, on command, and counts every attempt either way.</summary>
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

    /// <summary>Records that the calendar was reached, into a log a paired relay double shares.</summary>
    private sealed class OrderingCalendar(List<string> order) : ICalendarWriter
    {
        public IntegrationProvider Provider => IntegrationProvider.Google;

        public Task<bool> IsConnectedAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<PlacedEvent> CreateEventAsync(
            CalendarEventRequest request, CancellationToken cancellationToken)
        {
            order.Add(CalendarStep);
            return Task.FromResult(new PlacedEvent(EventId, CalendarUid));
        }

        public Task UpdateEventAsync(
            string externalEventId, CalendarEventRequest request, CancellationToken cancellationToken)
        {
            order.Add(CalendarStep);
            return Task.CompletedTask;
        }

        public Task CancelEventAsync(string externalEventId, CancellationToken cancellationToken)
        {
            order.Add(CalendarStep);
            return Task.CompletedTask;
        }
    }

    /// <summary>Records that the relay was reached, into the log its paired calendar shares.</summary>
    private sealed class OrderingRelay(List<string> order) : EmailSender(Options.Create(new EmailOptions()))
    {
        public override Task<string> SendAsync(MimeMessage message, CancellationToken cancellationToken)
        {
            order.Add(MailStep);
            return Task.FromResult(RelayAccepted);
        }
    }
}
