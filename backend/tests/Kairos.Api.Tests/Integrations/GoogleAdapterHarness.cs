using System.Text.Json;

using Kairos.Api.Integrations;
using Kairos.Api.Integrations.Google;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Tests.Features;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using NodaTime;

// Aliased to tell it from NodaTime's IClock.
using GoogleClock = Google.Apis.Util.IClock;

namespace Kairos.Api.Tests.Integrations;

/// <summary>
/// A real <see cref="GoogleCalendarAdapter"/>, resolved out of the real composition root, with a
/// connected Google account and a fake Google behind it.
/// </summary>
/// <remarks>
/// Built on <see cref="KairosApp"/> rather than by hand. Hand-wiring the adapter would mean
/// retyping the composition root into the test project, and the copy goes stale silently — the
/// failure being a registration that changes in Program.cs while every adapter test keeps passing
/// against the old shape.
/// </remarks>
internal sealed class GoogleAdapterHarness : IAsyncDisposable
{
    /// <summary>The connected account. Its address is also the primary calendar's id.</summary>
    private const string AccountLabel = "operator@example.com";

    private const string RefreshToken = "1//not-a-real-token";

    private const string MeetingTitle = "Intro call with Sam Rivers";

    private const string MeetingDescription = "Booked from the intro link.";

    private const string AttendeeName = "Sam Rivers";

    private const string AttendeeEmail = "sam@example.com";

    /// <summary>2026-09-14T09:00:00Z. Any instant; nothing about it decides a calendar.</summary>
    private static readonly Instant MeetingStart = Instant.FromUtc(2026, 9, 14, 9, 0);

    private const int MeetingMinutes = 30;

    private const string PatternName = "Weekdays";

    private const string LinkSlug = "intro";

    private const string LinkName = "Intro call";

    private const string AttendeeTimeZone = "Europe/Warsaw";

    /// <summary>Far enough ahead that every seeded meeting is still upcoming when a resync reads it.</summary>
    private static readonly Duration UpcomingLead = Duration.FromDays(1);

    private readonly KairosApp _app;
    private readonly IServiceScope _scope;
    private readonly IntegrationSettingStore _settings;

    private GoogleAdapterHarness(KairosApp app, IServiceScope scope)
    {
        _app = app;
        _scope = scope;
        Adapter = scope.ServiceProvider.GetRequiredService<GoogleCalendarAdapter>();
        _settings = scope.ServiceProvider.GetRequiredService<IntegrationSettingStore>();
    }

    public GoogleCalendarAdapter Adapter { get; }

    public GoogleClientFactory Clients =>
        _scope.ServiceProvider.GetRequiredService<GoogleClientFactory>();

    public T Resolve<T>()
        where T : notnull =>
        _scope.ServiceProvider.GetRequiredService<T>();

    public static async Task<GoogleAdapterHarness> CreateAsync(
        FakeGoogleHandler handler,
        GoogleClock? clock = null)
    {
        var app = new KairosApp(google: handler, googleClock: clock);
        var scope = app.Services.CreateScope();
        var harness = new GoogleAdapterHarness(app, scope);

        // Written through the store, never as a row of its own: the payload has to be encrypted
        // with this deployment's SecretProtector, and a hand-built row would decrypt to
        // credential_unreadable rather than to a connected account.
        await scope.ServiceProvider.GetRequiredService<IntegrationCredentialStore>()
            .WriteAsync(
                IntegrationProvider.Google,
                new StoredPayload(RefreshToken),
                AccountLabel,
                TestContext.Current.CancellationToken);

        return harness;
    }

    /// <summary>Writes the saved selection the way the endpoint would, straight to the store.</summary>
    public Task SaveBusyCalendarsAsync(params string[] calendars) =>
        _settings.WriteAsync(
            IntegrationProvider.Google,
            new Dictionary<string, JsonElement>
            {
                [GoogleCalendarAdapter.BusyCalendarsKey] =
                    JsonSerializer.SerializeToElement(calendars),
            },
            TestContext.Current.CancellationToken);

    /// <summary>The calendar the operator picked to write bookings to, saved the same way.</summary>
    public Task SaveWriteCalendarAsync(string calendar) =>
        _settings.WriteAsync(
            IntegrationProvider.Google,
            new Dictionary<string, JsonElement>
            {
                [GoogleCalendarAdapter.WriteCalendarKey] =
                    JsonSerializer.SerializeToElement(calendar),
            },
            TestContext.Current.CancellationToken);

    /// <summary>A booking as the sync service would hand one over. Nothing here is under test.</summary>
    public CalendarEventRequest SampleRequest(Guid bookingId) =>
        new(
            bookingId,
            MeetingStart,
            MeetingStart.Plus(Duration.FromMinutes(MeetingMinutes)),
            MeetingTitle,
            MeetingDescription,
            AttendeeName,
            AttendeeEmail);

    /// <summary>
    /// The operator's switch, thrown while this process is running. Written through the store for
    /// the reason the credential is, and offered here so a test can pause a provider the adapter
    /// has already used — which is the only state the switch was ever broken in.
    /// </summary>
    public Task PauseAsync() =>
        _scope.ServiceProvider.GetRequiredService<IntegrationCredentialStore>()
            .SetEnabledAsync(
                IntegrationProvider.Google, false, TestContext.Current.CancellationToken);

    /// <summary>The stored Google credential row, read fresh so no tracked copy can go stale.</summary>
    public async Task<IntegrationCredential> ReadCredentialAsync()
    {
        using var scope = _app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<KairosDbContext>()
            .IntegrationCredentials
            .AsNoTracking()
            .SingleAsync(
                credential => credential.Provider == IntegrationProvider.Google,
                TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Confirmed meetings on one link, back to back from <see cref="UpcomingLead"/> after the app's
    /// own clock, returned with the link and the booker a sync reads.
    /// </summary>
    public IReadOnlyList<Booking> SeedUpcomingBookings(int count)
    {
        using var scope = _app.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<KairosDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<IClock>().GetCurrentInstant();
        var meeting = Duration.FromMinutes(MeetingMinutes);

        var link = new Link
        {
            Id = Guid.CreateVersion7(),
            Slug = LinkSlug,
            Name = LinkName,
            Pattern = new Pattern { Id = Guid.CreateVersion7(), Name = PatternName },
        };

        var bookings = new List<Booking>();
        for (var index = 0; index < count; index++)
        {
            var start = now.Plus(UpcomingLead).Plus(meeting * index);
            var bookingId = Guid.CreateVersion7();
            bookings.Add(new Booking
            {
                Id = bookingId,
                Link = link,
                StartUtc = start.ToUnixTimeSeconds(),
                EndUtc = start.Plus(meeting).ToUnixTimeSeconds(),
                Status = BookingStatus.Confirmed,
                ManageTokenHash = bookingId.ToString(),
                CreatedAt = now.ToUnixTimeSeconds(),
                Participants =
                [
                    new BookingParticipant
                    {
                        Id = Guid.CreateVersion7(),
                        BookingId = bookingId,
                        Role = ParticipantRole.Booker,
                        Name = AttendeeName,
                        Email = AttendeeEmail,
                        TimeZoneId = AttendeeTimeZone,
                    },
                ],
            });
        }

        context.Bookings.AddRange(bookings);
        context.SaveChanges();

        return bookings;
    }

    /// <summary>The sync FollowUpWorker runs on a new booking, without the mail around it.</summary>
    public async Task SyncAsync(Booking booking)
    {
        using var scope = _app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CalendarSyncService>()
            .SyncAsync(booking, TestContext.Current.CancellationToken);
    }

    public IReadOnlyList<CalendarEntry> ReadEntries()
    {
        using var scope = _app.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<KairosDbContext>()
            .CalendarEntries
            .AsNoTracking()
            .ToList();
    }

    public IReadOnlyList<ConferenceLink> ReadConferenceLinks()
    {
        using var scope = _app.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<KairosDbContext>()
            .ConferenceLinks
            .AsNoTracking()
            .ToList();
    }

    public ValueTask DisposeAsync()
    {
        _scope.Dispose();
        _app.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The adapter's own payload shape, restated. <c>GoogleCredentialPayload</c> is private and
    /// nested inside <see cref="GoogleClientFactory"/> — deliberately, since it crosses no
    /// signature — so what is shared here is the one property name the store round-trips.
    /// </summary>
    private sealed record StoredPayload(string RefreshToken);
}
