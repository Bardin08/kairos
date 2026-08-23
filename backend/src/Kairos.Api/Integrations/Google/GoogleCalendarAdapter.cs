using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;

using Kairos.Api.Availability;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Scheduling;

using Microsoft.EntityFrameworkCore;

using NodaTime;

// Both aliases disambiguate a Google type of the same name — Google.Apis.Calendar.v3.Data.Settings
// is the account's calendar preferences, and NodaTime.Interval is not half-open.
using Interval = Kairos.Api.Scheduling.Interval;
using Settings = Kairos.Api.Persistence.Entities.Settings;

namespace Kairos.Api.Integrations.Google;

/// <summary>
/// One class because it is one account and one client; three interfaces because they are three
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
    ILogger<GoogleCalendarAdapter> logger) : IBusySource, ICalendarWriter, IConferenceCreator
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

            var response = await service.Freebusy
                .Query(new FreeBusyRequest
                {
                    TimeMinDateTimeOffset = window.Start.ToDateTimeOffset(),
                    TimeMaxDateTimeOffset = window.End.ToDateTimeOffset(),
                    Items = [new FreeBusyRequestItem { Id = PrimaryCalendarId }],
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
    public async Task<string> CreateEventAsync(
        CalendarEventRequest request,
        CancellationToken cancellationToken)
    {
        using var service = CreateService(await RequireCredentialAsync(cancellationToken));

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

        var insert = service.Events.Insert(body, PrimaryCalendarId);
        insert.ConferenceDataVersion = ConferenceDataVersion;

        var created = await insert.ExecuteAsync(cancellationToken);
        return created.Id;
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

        var moved = new Event
        {
            Summary = request.Title,
            Description = request.Description,
            Start = new EventDateTime { DateTimeDateTimeOffset = request.Start.ToDateTimeOffset() },
            End = new EventDateTime { DateTimeDateTimeOffset = request.End.ToDateTimeOffset() },
        };

        await service.Events
            .Patch(moved, PrimaryCalendarId, externalEventId)
            .ExecuteAsync(cancellationToken);
    }

    public async Task CancelEventAsync(string externalEventId, CancellationToken cancellationToken)
    {
        using var service = CreateService(await RequireCredentialAsync(cancellationToken));
        await service.Events.Delete(PrimaryCalendarId, externalEventId).ExecuteAsync(cancellationToken);
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

        var created = await service.Events
            .Get(PrimaryCalendarId, EventId(bookingId))
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

    /// <summary>
    /// The booking id as Google spells an event id: thirty-two lowercase hex characters, which
    /// is a strict subset of the base32hex alphabet the API requires.
    /// </summary>
    private static string EventId(Guid bookingId) => bookingId.ToString("N");

    private static CalendarService CreateService(UserCredential credential) =>
        new(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = ApplicationName,
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
