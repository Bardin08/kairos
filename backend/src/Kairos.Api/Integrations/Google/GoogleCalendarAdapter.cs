using System.Text.Json;

using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;

using Kairos.Api.Availability;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Scheduling;
using Kairos.Api.Shared;

using Microsoft.EntityFrameworkCore;

using NodaTime;

// The first two aliases disambiguate a Google type of the same name —
// Google.Apis.Calendar.v3.Data.Settings is the account's calendar preferences, and NodaTime.Interval
// is not half-open. The third names Google's factory interface, which collides with the one in
// System.Net.Http.
using GoogleHttpFactory = Google.Apis.Http.IHttpClientFactory;
using Interval = Kairos.Api.Scheduling.Interval;
using Settings = Kairos.Api.Persistence.Entities.Settings;

namespace Kairos.Api.Integrations.Google;

/// <summary>
/// One class because it is one account and one client; four interfaces because they are four
/// capabilities and a provider that only did one of them would implement only that one.
/// </summary>
/// <remarks>
/// Freebusy rather than the events list on the read side: one call for the whole window, no
/// pagination, recurrences expanded server-side, intervals rather than events — all availability
/// wants, and the organizer's private titles and attendees never enter this process.
///
/// The read and write paths fail differently on purpose, and the catch below is scoped to the
/// read alone. A failed freebusy call is one source with no opinion; a failed calendar write is a
/// booking with no meeting, and swallowing it here would hide it from
/// <see cref="CalendarSyncService"/> and its <see cref="CalendarSyncState.Failed"/> row.
/// </remarks>
public sealed class GoogleCalendarAdapter(
    GoogleClientFactory clients,
    KairosDbContext context,
    IntegrationSettingStore settings,
    GoogleHttpFactory httpClients,
    ILogger<GoogleCalendarAdapter> logger)
    : IBusySource, ICalendarWriter, IConferenceCreator, IIntegrationSettings
{
    /// <summary>The signed-in account's own calendar; its id is that account's address.</summary>
    private const string PrimaryCalendarId = "primary";

    private const string ApplicationName = "Kairos";

    /// <summary>
    /// Version 1 of the conference schema. Without it Google silently ignores
    /// <c>ConferenceData</c> on an insert and returns an event with no Meet — the failure that
    /// looks like a working integration.
    /// </summary>
    private const int ConferenceDataVersion = 1;

    /// <summary>Google Meet. The other solution keys are the retired Hangouts products.</summary>
    private const string MeetSolution = "hangoutsMeet";

    /// <summary>Of the entry points on a conference — the others are phone and SIP.</summary>
    private const string VideoEntryPoint = "video";

    /// <summary>
    /// Google's own ceiling on one freebusy query: <c>calendarExpansionMax</c> is documented with
    /// a maximum value of 50. Enforced on save, where the operator is looking at the list and can
    /// decide which calendars matter — never by truncating a read months later.
    /// </summary>
    public const int MaxFreebusyCalendars = 50;

    /// <summary>The set busy time is read from, as the saved settings are keyed.</summary>
    public const string BusyCalendarsKey = "busyCalendars";

    /// <summary>The one bookings are written to.</summary>
    public const string WriteCalendarKey = "writeCalendar";

    private const string BusyCalendarsLabel = "Read busy time from";

    private const string BusyCalendarsHelp =
        "A slot is offered only when every calendar here is free. A calendar you add in Google "
        + "later does not join by itself.";

    private const string WriteCalendarLabel = "Write bookings to";

    private const string WriteCalendarHelp = "Only calendars you can write to are offered.";

    private const string PrimaryDetail = "Primary";

    private const string ReadOnlyDetail = "Read-only subscription";

    private const string DepartedDetail = "No longer reachable";

    /// <summary>The two access roles that can hold an event, as Google spells them.</summary>
    private const string OwnerRole = "owner";

    private const string WriterRole = "writer";

    public IntegrationProvider Provider => IntegrationProvider.Google;

    /// <summary>
    /// Every busy period Google reports over <paramref name="window"/> carries the <em>global</em>
    /// margin: an external event has no Kairos link to state one. <see cref="SlotGenerator"/>
    /// still takes the larger of this and the querying link's margin.
    /// </summary>
    /// <remarks>
    /// No cache. The poll runs per page load — the spec chose that over push channels, which
    /// need a public callback URL, a renewal timer and a store of channel ids to buy freshness
    /// this does not need.
    /// </remarks>
    public async Task<IReadOnlyList<BusyBlock>> GetBusyAsync(
        Interval window,
        CancellationToken cancellationToken)
    {
        try
        {
            // Null is the ordinary state of this deployment, not a fault: no client configured,
            // or configured and never connected. Both mean Google has nothing to say about
            // whether the organizer is free.
            var credential = await clients.GetCredentialAsync(cancellationToken);
            if (credential is null)
            {
                return [];
            }

            using var service = CreateService(credential);

            var calendars = await ResolveBusyCalendarsAsync(cancellationToken);

            var response = await service.Freebusy
                .Query(new FreeBusyRequest
                {
                    TimeMinDateTimeOffset = window.Start.ToDateTimeOffset(),
                    TimeMaxDateTimeOffset = window.End.ToDateTimeOffset(),
                    Items = [.. calendars.Select(id => new FreeBusyRequestItem { Id = id })],
                })
                .ExecuteAsync(cancellationToken);

            var marginMinutes = await context.Settings.AsNoTracking()
                .Where(row => row.Id == Settings.SingletonId)
                .Select(row => row.MarginMinutes)
                .SingleAsync(cancellationToken);

            return Translate(response, marginMinutes);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The trade: an empty set is not "the organizer is free", it is "this source has no
            // opinion". Slots a real Google event covers will be offered and the booking that
            // takes one WILL be written, since Kairos has no record of the conflict. Still the
            // right side to fail on for a page a stranger is looking at: the alternative returns
            // 500 over a third party's outage, costing every booking rather than the overlapping
            // few.
            //
            // Logged at Warning with the window, because this is the line that explains a booking
            // the organizer cannot account for.
            logger.LogWarning(
                exception,
                "Google freebusy failed for {Start}..{End}; treating Google as having no busy time. "
                + "Slots covered by a real Google event may be offered and booked.",
                window.Start,
                window.End);

            return [];
        }
    }

    public async Task<bool> IsConnectedAsync(CancellationToken cancellationToken) =>
        await clients.GetCredentialAsync(cancellationToken) is not null;

    /// <remarks>
    /// Two idempotency keys, both the booking id, guarding different things.
    /// <c>CreateConferenceRequest.RequestId</c> makes the <em>conference</em> idempotent: a retry
    /// presents the same id and Google returns the conference it already minted. The event's own
    /// id makes the <em>event</em> idempotent: a retry is refused as a duplicate. A UUID's hex is
    /// thirty-two characters from 0-9a-f and Google's event ids are base32hex — lowercase a-v and
    /// 0-9, five to 1024 long — so the booking id is a legal event id as it stands, and choosing
    /// it lets <see cref="CreateAsync"/> find this event again knowing only the booking.
    /// </remarks>
    public async Task<PlacedEvent> CreateEventAsync(
        CalendarEventRequest request,
        CancellationToken cancellationToken)
    {
        using var service = CreateService(await RequireCredentialAsync(cancellationToken));
        var calendar = await ResolveWriteCalendarAsync(cancellationToken);

        var body = new Event
        {
            Id = EventId(request.BookingId),
            Summary = request.Title,
            Description = request.Description,

            // The offset-carrying overload, not the string one: an instant with its offset is
            // unambiguous, so no time zone has to be named and none can be named wrongly.
            Start = new EventDateTime { DateTimeDateTimeOffset = request.Start.ToDateTimeOffset() },
            End = new EventDateTime { DateTimeDateTimeOffset = request.End.ToDateTimeOffset() },
            Attendees =
            [
                new EventAttendee
                {
                    Email = request.AttendeeEmail,
                    DisplayName = request.AttendeeName,
                },
            ],
            ConferenceData = new ConferenceData
            {
                CreateRequest = new CreateConferenceRequest
                {
                    RequestId = request.BookingId.ToString(),
                    ConferenceSolutionKey = new ConferenceSolutionKey { Type = MeetSolution },
                },
            },
        };

        var insert = service.Events.Insert(body, calendar);
        insert.ConferenceDataVersion = ConferenceDataVersion;

        var created = await insert.ExecuteAsync(cancellationToken);

        // Read, never assembled. Google mints the UID on the insert and will not accept one there:
        // the API documents iCalUID as supplied "when importing events via the import method", and
        // that only one of iCalUID and id may be given at creation time.
        return new PlacedEvent(created.Id, created.ICalUID);
    }

    /// <remarks>
    /// <c>Patch</c> rather than <c>Update</c>, and the difference is this method's correctness.
    /// Update replaces the resource, clearing every field absent from the body — here the attendee
    /// list, the answers those attendees have given, and the <c>conferenceData</c> carrying the
    /// Meet the invitee has a link to. Patch merges.
    ///
    /// Attendees are deliberately not written even though the request carries one: a reschedule
    /// keeps its booker, and re-sending the list is how a Google event loses everyone's RSVP for
    /// no gain. <c>conferenceDataVersion</c> is likewise absent, only being read when the body
    /// touches conference data.
    /// </remarks>
    public async Task UpdateEventAsync(
        string externalEventId,
        CalendarEventRequest request,
        CancellationToken cancellationToken)
    {
        using var service = CreateService(await RequireCredentialAsync(cancellationToken));
        var calendar = await ResolveWriteCalendarAsync(cancellationToken);

        var moved = new Event
        {
            Summary = request.Title,
            Description = request.Description,
            Start = new EventDateTime { DateTimeDateTimeOffset = request.Start.ToDateTimeOffset() },
            End = new EventDateTime { DateTimeDateTimeOffset = request.End.ToDateTimeOffset() },
        };

        await service.Events
            .Patch(moved, calendar, externalEventId)
            .ExecuteAsync(cancellationToken);
    }

    public async Task CancelEventAsync(string externalEventId, CancellationToken cancellationToken)
    {
        using var service = CreateService(await RequireCredentialAsync(cancellationToken));
        var calendar = await ResolveWriteCalendarAsync(cancellationToken);

        await service.Events.Delete(calendar, externalEventId).ExecuteAsync(cancellationToken);
    }

    /// <summary>
    /// A read rather than a write, and not a shortcut: with Google, conferencing is requested as
    /// part of the event insert and provisioned <em>asynchronously</em>, so the insert can come
    /// back with the request still pending and no entry points on it. Re-reading the event is how
    /// the API documents finding out what the conference turned into, and the deterministic event
    /// id from <see cref="CreateEventAsync"/> makes it reachable from a booking id alone.
    /// </summary>
    public async Task<ConferenceDetails> CreateAsync(
        Guid bookingId,
        CancellationToken cancellationToken)
    {
        using var service = CreateService(await RequireCredentialAsync(cancellationToken));
        var calendar = await ResolveWriteCalendarAsync(cancellationToken);

        var created = await service.Events
            .Get(calendar, EventId(bookingId))
            .ExecuteAsync(cancellationToken);

        var conference = created.ConferenceData
            ?? throw new InvalidOperationException(
                $"Google created the event for booking {bookingId} without conference data.");

        var join = conference.EntryPoints?
            .FirstOrDefault(entry => entry.EntryPointType == VideoEntryPoint)?.Uri;

        if (string.IsNullOrEmpty(join))
        {
            // Almost always the pending case: provisioning had not finished when this read ran.
            // Reported rather than retried — there is no retry loop to hang a poll off yet, and
            // the caller already treats a missing conference as a meeting with no join link.
            throw new InvalidOperationException(
                $"Google returned no video entry point for booking {bookingId}; the conference "
                + $"request is {conference.CreateRequest?.Status?.StatusCode ?? "absent"}.");
        }

        return new ConferenceDetails(conference.ConferenceId, new Uri(join));
    }

    public async Task<IReadOnlyList<SettingField>> DescribeSettingsAsync(
        CancellationToken cancellationToken)
    {
        var saved = await settings.ReadAsync(Provider, cancellationToken);
        var chosen = Chosen(saved);
        var entries = await ListCalendarsAsync(cancellationToken);

        // Null and empty are different answers, and the field is where the difference is said:
        // null is "Google could not be reached", so the panel keeps drawing the saved value
        // instead of offering to clear a selection Kairos is still using.
        IReadOnlyList<SettingOption>? readable = null;
        IReadOnlyList<SettingOption>? writable = null;

        if (entries is not null)
        {
            var offered = entries.Select(Option).ToList();

            // A saved calendar Google no longer lists is still in force, so it is still shown.
            // Synthesised here rather than left to the renderer: the frontend knows no provider,
            // and a calendar id is a provider's fact about a provider's account.
            offered.AddRange(chosen
                .Where(id => entries.All(entry => entry.Id != id))
                .Select(id => new SettingOption(id, id, DepartedDetail)));

            readable = offered;
            writable = entries.Where(CanWrite).Select(Option).ToList();
        }

        return
        [
            new SettingField(
                BusyCalendarsKey,
                SettingFieldKind.MultiSelect,
                BusyCalendarsLabel,
                BusyCalendarsHelp,
                readable,
                MaxFreebusyCalendars,
                Required: false,
                JsonSerializer.SerializeToElement(chosen)),

            new SettingField(
                WriteCalendarKey,
                SettingFieldKind.Select,
                WriteCalendarLabel,
                WriteCalendarHelp,
                writable,
                MaxValues: null,
                Required: true,
                JsonSerializer.SerializeToElement(WriteTarget(saved))),
        ];
    }

    /// <remarks>
    /// Shape first, then the account. Everything a key or a value can get wrong on its own is
    /// settled before Google is asked anything, so an unknown key is refused during an outage
    /// rather than answered with a sentence about connectivity that has nothing to do with it.
    /// </remarks>
    public async Task SaveSettingsAsync(
        IReadOnlyDictionary<string, JsonElement> values,
        CancellationToken cancellationToken)
    {
        var chosen = new List<string>();
        var target = default(string);

        foreach (var (key, value) in values)
        {
            switch (key)
            {
                case BusyCalendarsKey:
                    chosen = Ids(value, $"'{key}' takes a list of calendar ids.");

                    if (chosen.Count > MaxFreebusyCalendars)
                    {
                        throw DomainException.Invalid(
                            IIntegrationSettings.SettingTooManyCode,
                            $"Kairos can read busy time from at most {MaxFreebusyCalendars} "
                            + "calendars.");
                    }

                    break;

                case WriteCalendarKey:
                    target = Id(value, $"'{key}' takes one calendar id.");
                    break;

                default:
                    // Unknown, not invalid. The writer forwards every key it does not own straight
                    // here without inspecting it, so this adapter is the only thing in the process
                    // that can tell a key nobody describes from a value nobody accepts — and
                    // IIntegrationSettings gives those two different codes.
                    throw DomainException.Invalid(
                        IIntegrationSettings.SettingUnknownCode,
                        $"Google has no setting called '{key}'.");
            }
        }

        var entries = await ListCalendarsAsync(cancellationToken)
            ?? throw DomainException.Invalid(
                IIntegrationSettings.SettingInvalidCode,
                "Google could not be reached, so there is nothing to check these against.");

        var known = entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var unknown = chosen.FirstOrDefault(id => !known.Contains(id));

        if (unknown is not null)
        {
            throw DomainException.Invalid(
                IIntegrationSettings.SettingInvalidCode,
                $"'{unknown}' is not a calendar on this Google account.");
        }

        if (target is not null && !entries.Any(entry => CanWrite(entry) && entry.Id == target))
        {
            throw DomainException.Invalid(
                IIntegrationSettings.SettingInvalidCode,
                $"'{target}' is not a calendar Kairos can write bookings to.");
        }

        await settings.WriteAsync(Provider, values, cancellationToken);
    }

    /// <summary>
    /// The booking id as Google spells an event id: thirty-two lowercase hex characters, which
    /// is a strict subset of the base32hex alphabet the API requires.
    /// </summary>
    private static string EventId(Guid bookingId) => bookingId.ToString("N");

    private static SettingOption Option(CalendarListEntry entry) =>
        new(entry.Id, entry.SummaryOverride ?? entry.Summary ?? entry.Id, Detail(entry));

    /// <summary>
    /// What the option says about itself under its own label. Primary first, because an operator
    /// with a dozen calendars is looking for that one and it is also the only id that is always
    /// right.
    /// </summary>
    private static string? Detail(CalendarListEntry entry)
    {
        if (entry.Primary == true)
        {
            return PrimaryDetail;
        }

        return CanWrite(entry) ? null : ReadOnlyDetail;
    }

    /// <summary>
    /// Whether an event can be put in it. Busy time is readable from any calendar on the account;
    /// an insert into a subscription is refused, so offering one to write to would be offering a
    /// choice that cannot work.
    /// </summary>
    private static bool CanWrite(CalendarListEntry entry) =>
        entry.AccessRole is OwnerRole or WriterRole;

    private static List<string> Ids(JsonElement value, string complaint)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw DomainException.Invalid(IIntegrationSettings.SettingInvalidCode, complaint);
        }

        return [.. value.EnumerateArray().Select(item => Id(item, complaint))];
    }

    private static string Id(JsonElement value, string complaint) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw DomainException.Invalid(IIntegrationSettings.SettingInvalidCode, complaint);

    /// <summary>
    /// The calendars busy time is read from, as saved. Anything malformed reads as nothing chosen
    /// rather than throwing: this is on the path a stranger's page load takes, and a settings row
    /// somebody edited by hand must not be able to stop availability answering.
    /// </summary>
    private static List<string> Chosen(IReadOnlyDictionary<string, JsonElement> saved) =>
        saved.TryGetValue(BusyCalendarsKey, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)]
            : [];

    private static string? WriteTarget(IReadOnlyDictionary<string, JsonElement> saved) =>
        saved.TryGetValue(WriteCalendarKey, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Which calendars busy time is read from: the operator's choice, or the default worked out
    /// once and then written down.
    /// </summary>
    /// <remarks>
    /// The write is on a read path, and it is a cost taken deliberately. It happens once per
    /// deployment, it is idempotent, and failing it is not fatal — this request still uses the set
    /// it computed and the next load tries again. The alternative recomputes the default on every
    /// view of an anonymous page, paying a second Google round trip forever to keep a property
    /// the operator's first save ends anyway.
    ///
    /// What that costs is honest and worth saying: once the row exists, a calendar created in
    /// Google afterwards does not join by itself. The operator adds it on the settings panel,
    /// which is what <see cref="BusyCalendarsHelp"/> tells them.
    /// </remarks>
    private async Task<IReadOnlyList<string>> ResolveBusyCalendarsAsync(
        CancellationToken cancellationToken)
    {
        var chosen = Chosen(await settings.ReadAsync(Provider, cancellationToken));
        if (chosen.Count > 0)
        {
            return chosen;
        }

        var entries = await ListCalendarsAsync(cancellationToken);
        if (entries is null)
        {
            // Google would not say which calendars exist. The account's own is the one id that is
            // always right, and nothing is written down on a guess.
            return [PrimaryCalendarId];
        }

        // Google's own idea of which calendars the account cares about — the ones ticked in its
        // sidebar. Capped rather than refused: this set is being materialised, so the operator
        // opens the panel to a selection that is exactly what Kairos is using.
        var selected = entries
            .Where(entry => entry.Selected == true)
            .Select(entry => entry.Id)
            .Take(MaxFreebusyCalendars)
            .ToList();

        if (selected.Count == 0)
        {
            return [PrimaryCalendarId];
        }

        try
        {
            await settings.WriteAsync(
                Provider,
                new Dictionary<string, JsonElement>
                {
                    [BusyCalendarsKey] = JsonSerializer.SerializeToElement(selected),
                },
                cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                exception,
                "Could not record the default Google calendar selection; using it for this "
                + "request and working it out again on the next.");
        }

        return selected;
    }

    /// <summary>
    /// Which calendar a booking is put in, moved in, withdrawn from and read back out of — the
    /// operator's choice, and the primary calendar until they make one.
    /// </summary>
    /// <remarks>
    /// The fallback is not a default so much as the behaviour every deployment already has: before
    /// this setting existed every booking went to the primary calendar, and a save nobody has made
    /// must not move them. Nothing is written down here, unlike
    /// <see cref="ResolveBusyCalendarsAsync"/> — a materialised default would be a second place the
    /// answer lives, and there is no round trip to save by having one.
    /// </remarks>
    private async Task<string> ResolveWriteCalendarAsync(CancellationToken cancellationToken) =>
        WriteTarget(await settings.ReadAsync(Provider, cancellationToken)) ?? PrimaryCalendarId;

    /// <summary>
    /// Every calendar on the account, or null when Google could not be asked. Null rather than an
    /// exception because the caller describing settings has something useful to show without it.
    /// </summary>
    private async Task<IReadOnlyList<CalendarListEntry>?> ListCalendarsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var credential = await clients.GetCredentialAsync(cancellationToken);
            if (credential is null)
            {
                return null;
            }

            using var service = CreateService(credential);
            var list = await service.CalendarList.List().ExecuteAsync(cancellationToken);

            return list.Items?.Where(entry => entry.Deleted != true).ToList() ?? [];
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Reading the Google calendar list failed.");
            return null;
        }
    }

    /// <remarks>
    /// The factory is injected rather than left to the client library's default, and the only
    /// reason is testability: it is the one hook the library offers for standing in front of the
    /// network, and without it the code that builds a request — the code this class asks the wrong
    /// question in — has no test that can see it. Production registers the library's own.
    /// </remarks>
    private CalendarService CreateService(UserCredential credential) =>
        new(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = ApplicationName,
            HttpClientFactory = httpClients,
        });

    /// <summary>
    /// The write path's counterpart to the read path's null check. A write against no account has
    /// nothing to return, so it throws and <see cref="CalendarSyncService"/> records it. Reaching
    /// here means the account was disconnected between the connectivity check and the write.
    /// </summary>
    private async Task<UserCredential> RequireCredentialAsync(CancellationToken cancellationToken) =>
        await clients.GetCredentialAsync(cancellationToken)
        ?? throw new InvalidOperationException("No Google account is connected.");

    /// <summary>
    /// Over <c>Values</c> rather than the <c>primary</c> key: the response is keyed by the id that
    /// was asked for, and only one was, so iterating removes an assumption about how Google echoes
    /// an alias back instead of restating it.
    /// </summary>
    private List<BusyBlock> Translate(FreeBusyResponse response, int marginMinutes)
    {
        var busy = new List<BusyBlock>();
        if (response.Calendars is null)
        {
            return busy;
        }

        foreach (var (id, calendar) in response.Calendars)
        {
            // A per-calendar error arrives inside an HTTP 200 with an empty Busy list, so without
            // this a revoked scope or a rate limit is indistinguishable from a free week.
            if (calendar.Errors is { Count: > 0 } errors)
            {
                logger.LogWarning(
                    "Google reported no busy time for calendar {Calendar}: {Reasons}.",
                    id,
                    string.Join(", ", errors.Select(error => error.Reason)));
            }

            if (calendar.Busy is null)
            {
                continue;
            }

            foreach (var period in calendar.Busy)
            {
                // Google documents both bounds as present on a busy period. Skipped rather than
                // asserted: a half-open period is not a reason to drop the other twenty.
                if (period.StartDateTimeOffset is not { } start
                    || period.EndDateTimeOffset is not { } end)
                {
                    continue;
                }

                busy.Add(new BusyBlock(
                    new Interval(Instant.FromDateTimeOffset(start), Instant.FromDateTimeOffset(end)),
                    marginMinutes));
            }
        }

        return busy;
    }
}
