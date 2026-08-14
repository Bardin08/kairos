using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using NodaTime;

namespace Kairos.Api.Integrations;

/// <summary>
/// Everything that happens to a booking after the domain has accepted it: it goes into a
/// calendar, it gets somewhere to meet, and both outcomes are recorded. This is the file the
/// provider-neutrality of Part B is judged on, so read its usings — there is no vendor namespace
/// among them, and adding one would be the bug.
/// </summary>
/// <remarks>
/// It never throws. Ground rule 14 in one sentence: a booking the domain accepted is never lost
/// because a third party had a bad minute, and the only way to mean that is for every path out of
/// here to be a return. A provider that refuses becomes a <see cref="CalendarSyncState.Failed"/>
/// row with the reason on it; a provider nobody connected becomes nothing at all.
/// </remarks>
public sealed class CalendarSyncService(
    ICalendarWriter calendars,
    IConferenceCreator conferences,
    KairosDbContext context,
    IClock clock,
    ILogger<CalendarSyncService> logger)
{
    /// <summary>Between the link's name and the booker's, in the title the organizer will read.</summary>
    private const string TitleJoiner = " with ";

    /// <summary>
    /// Writes <paramref name="booking"/> to whatever is connected and records what happened.
    /// The booking must arrive with its link and participants loaded — this reads a meeting, not
    /// a row, and re-fetching what the caller already has would be a second query on the create
    /// path for nothing.
    /// </summary>
    public async Task SyncAsync(Booking booking, CancellationToken cancellationToken)
    {
        try
        {
            await WriteAsync(booking, cancellationToken);
        }
        catch (Exception exception)
        {
            // The outer net, and the reason SyncAsync has no failure of its own to report: by the
            // time it runs the booking is committed and the caller is about to answer 201. An
            // exception escaping here would turn a booking that exists into a 500 the booker
            // reads as "it did not work", which is the exact outcome ground rule 14 forbids.
            logger.LogError(
                exception,
                "Recording the calendar sync for booking {Booking} failed. The booking stands; its "
                + "sync state is now unknown rather than wrong.",
                booking.Id);
        }
    }

    private async Task WriteAsync(Booking booking, CancellationToken cancellationToken)
    {
        string externalEventId;
        try
        {
            // Asked before anything is attempted, because "nobody connected a calendar" is not a
            // failure and must not be recorded as one. See the class remarks on why the absence
            // of a row is the honest answer there.
            if (!await calendars.IsConnectedAsync(cancellationToken))
            {
                return;
            }

            externalEventId = await calendars.CreateEventAsync(Describe(booking), cancellationToken);
        }
        catch (Exception exception)
        {
            await RecordFailureAsync(booking.Id, exception);
            return;
        }

        context.CalendarEntries.Add(new CalendarEntry
        {
            BookingId = booking.Id,
            Provider = calendars.Provider,
            ExternalEventId = externalEventId,
            State = CalendarSyncState.Synced,
            UpdatedAt = Now(),
        });

        await AddConferenceAsync(booking.Id, cancellationToken);

        // One SaveChanges, so the entry and the link commit together — a join URL recorded
        // without the event that carries it would be a meeting nobody can find twice over.
        // Uncancellable: this describes work a third party has already done, and a booker who
        // closed the tab is not a reason to lose the only record that it happened.
        await context.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// Best effort, and separately so. The event is already in the calendar at this point, so a
    /// conferencing failure must not downgrade a real <see cref="CalendarSyncState.Synced"/> to
    /// <see cref="CalendarSyncState.Failed"/> — that would report the one thing that worked as
    /// broken. <see cref="ConferenceLink"/> has no state column for the same reason: the row
    /// exists or it does not, and its absence is what "no join link" looks like.
    /// </summary>
    private async Task AddConferenceAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        try
        {
            var conference = await conferences.CreateAsync(bookingId, cancellationToken);
            context.ConferenceLinks.Add(new ConferenceLink
            {
                BookingId = bookingId,
                Provider = conferences.Provider,
                ExternalId = conference.ExternalId,
                JoinUrl = conference.JoinUrl.ToString(),
                CreatedAt = Now(),
            });
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "No conference for booking {Booking}. The meeting is in the calendar with no way "
                + "to join it remotely.",
                bookingId);
        }
    }

    private async Task RecordFailureAsync(Guid bookingId, Exception exception)
    {
        logger.LogWarning(
            exception, "Calendar sync failed for booking {Booking}. The booking stands.", bookingId);

        context.CalendarEntries.Add(new CalendarEntry
        {
            BookingId = bookingId,
            Provider = calendars.Provider,
            State = CalendarSyncState.Failed,
            LastError = exception.Message,
            UpdatedAt = Now(),
        });

        // Uncancellable for the same reason as the success path, and more so: this row is the
        // only thing that will tell the operator their calendar is not receiving bookings.
        await context.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// The translation, and the only place a meeting becomes a calendar entry's worth of words.
    /// It reads the booking and nothing else — no settings, no formatting rules, no provider.
    /// </summary>
    private static CalendarEventRequest Describe(Booking booking)
    {
        var booker = booking.Participants.First(participant => participant.Role == ParticipantRole.Booker);

        return new CalendarEventRequest(
            booking.Id,
            Instant.FromUnixTimeSeconds(booking.StartUtc),
            Instant.FromUnixTimeSeconds(booking.EndUtc),
            booking.Link.Name + TitleJoiner + booker.Name,
            booking.Notes,
            booker.Name,
            booker.Email);
    }

    private long Now() => clock.GetCurrentInstant().ToUnixTimeSeconds();
}
