using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;

using Kairos.Api.Availability;
using Kairos.Api.Persistence;
using Kairos.Api.Scheduling;

using Microsoft.EntityFrameworkCore;

using NodaTime;

// Both aliases disambiguate a Google type of the same name — Google.Apis.Calendar.v3.Data.Settings
// is the account's calendar preferences, and NodaTime.Interval is not half-open.
using Interval = Kairos.Api.Scheduling.Interval;
using Settings = Kairos.Api.Persistence.Entities.Settings;

namespace Kairos.Api.Integrations.Google;

/// <summary>
/// The connected Google account's calendar as a second source of busy time, so a meeting booked
/// anywhere else suppresses the slots it covers here. Registering it is the whole integration —
/// <see cref="Kairos.Api.Availability.SlotQuery"/> already drains every <see cref="IBusySource"/>,
/// and nothing in <c>Scheduling/</c> knows this type exists.
/// </summary>
/// <remarks>
/// Freebusy rather than the events list: it is one call for the whole window, it needs no
/// pagination, it expands recurrences server-side, and it returns intervals rather than events —
/// which is all availability wants, and it means the titles and attendees of the organizer's
/// private calendar never enter this process.
/// </remarks>
public sealed class GoogleCalendarAdapter(
    GoogleClientFactory clients,
    KairosDbContext context,
    ILogger<GoogleCalendarAdapter> logger) : IBusySource
{
    /// <summary>The signed-in account's own calendar; its id is that account's address.</summary>
    private const string PrimaryCalendarId = "primary";

    private const string ApplicationName = "Kairos";

    /// <summary>
    /// Every busy period Google reports over <paramref name="window"/>, each carrying the
    /// <em>global</em> margin: an external event has no Kairos link behind it to state one, and
    /// the organizer's global setting is the only answer that exists for it.
    /// <see cref="SlotGenerator"/> still takes the larger of this and the querying link's margin,
    /// so a link that wants more breathing room than the global still gets it.
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

            using var service = new CalendarService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName,
            });

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
            // The trade, stated plainly: an empty set is not "the organizer is free", it is "this
            // source has no opinion". Slots that a real Google event covers will be offered, and
            // the booking that takes one WILL be written — Kairos has no record of the conflict,
            // so nothing downstream can refuse it. That is still the right side to fail on for a
            // page a stranger is looking at: the alternative is a public booking page that returns
            // 500 because a third party is having an outage, which costs every booking rather than
            // the few that overlap. Double-booked and recoverable beats unreachable.
            //
            // Not swallowed silently: this is the line that explains a booking the organizer
            // cannot account for, so it is logged at Warning with the window it was asked about.
            logger.LogWarning(
                exception,
                "Google freebusy failed for {Start}..{End}; treating Google as having no busy time. "
                + "Slots covered by a real Google event may be offered and booked.",
                window.Start,
                window.End);

            return [];
        }
    }

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
