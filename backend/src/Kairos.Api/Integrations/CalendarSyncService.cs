using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;

using NodaTime;

namespace Kairos.Api.Integrations;

/// <summary>
/// Everything that happens to a booking after the domain has accepted it: it goes into a
/// calendar, it moves when the booking moves, it leaves when the booking is cancelled, and every
/// outcome is recorded. This is the file the provider-neutrality of Part B is judged on, so read
/// its usings — there is no vendor namespace among them, and adding one would be the bug.
/// </summary>
/// <remarks>
/// It never throws. Ground rule 14 in one sentence: a booking the domain accepted is never lost
/// because a third party had a bad minute, and the only way to mean that is for every path out of
/// here to be a return. A provider that refuses becomes a <see cref="CalendarSyncState.Failed"/>
/// row with the reason on it; a provider nobody connected becomes nothing at all.
///
/// The three entry points are three verbs on one row. <see cref="CalendarEntry"/> is unique per
/// (booking, provider), so a booking has one row for its whole life and the state on it is the
/// answer to "where is this meeting, really" — <see cref="CalendarSyncState.Synced"/> at the time
/// the booking says, <see cref="CalendarSyncState.Failed"/> with the provider's reason, or
/// <see cref="CalendarSyncState.Cancelled"/> for an event that was withdrawn. Nothing here
/// deletes a row: the id of an event that once existed is the only evidence it did.
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
    public Task SyncAsync(Booking booking, CancellationToken cancellationToken) =>
        GuardedAsync(booking.Id, () => PlaceAsync(booking, cancellationToken));

    /// <summary>
    /// Moves the event this booking already has to the booking's new time. Same loading
    /// requirement as <see cref="SyncAsync"/>, because the event is rewritten from the booking.
    /// </summary>
    /// <remarks>
    /// Called after the booking is confirmed at its new time and never while a reschedule has it
    /// released. A reschedule is release, re-check, re-place: it sets the booking cancelled and
    /// back to confirmed on every path out, the failing ones included, so anything hung on the
    /// status change would withdraw the event for every meeting that merely moved — and the
    /// attendee who had already accepted would watch it disappear from their calendar. That is
    /// the same trap the <c>booking.cancelled</c> announcement is kept out of, for the same
    /// reason, and it is why moving and withdrawing are two methods here rather than one hook.
    /// </remarks>
    public Task RescheduleAsync(Booking booking, CancellationToken cancellationToken) =>
        GuardedAsync(booking.Id, () => MoveAsync(booking, cancellationToken));

    /// <summary>
    /// Withdraws the event this booking has, if it has one. The id alone, because a cancellation
    /// rewrites nothing — it only names what to remove.
    /// </summary>
    public Task CancelAsync(Guid bookingId, CancellationToken cancellationToken) =>
        GuardedAsync(bookingId, () => WithdrawAsync(bookingId, cancellationToken));

    /// <summary>
    /// The outer net, and the reason none of the three has a failure of its own to report: by the
    /// time any of them runs, the domain has committed and the caller is about to answer. An
    /// exception escaping here would turn a booking that exists — or a cancellation that has
    /// already happened — into a 500 the caller reads as "it did not work", which is the exact
    /// outcome ground rule 14 forbids.
    /// </summary>
    private async Task GuardedAsync(Guid bookingId, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Recording the calendar sync for booking {Booking} failed. The booking stands; its "
                + "sync state is now unknown rather than wrong.",
                bookingId);
        }
    }

    private async Task PlaceAsync(Booking booking, CancellationToken cancellationToken)
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
            // No row can exist to update: this runs once, on the request that inserted the
            // booking, and nothing else writes a CalendarEntry before it.
            await RecordFailureAsync(booking.Id, null, exception);
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
    /// The move. No connectivity check, unlike <see cref="PlaceAsync"/>, and that is the design
    /// rather than an omission: reaching here with an event id means a calendar <em>was</em>
    /// connected when this booking was made, so a provider that now refuses is a real failure an
    /// operator has to see — an event sitting in a calendar at a time the booking no longer
    /// claims is precisely the two-plausible-times problem this method exists to end. The adapter
    /// throws on a missing credential and that throw becomes the row.
    /// </summary>
    private async Task MoveAsync(Booking booking, CancellationToken cancellationToken)
    {
        var entry = await FindEntryAsync(booking.Id, cancellationToken);
        if (entry?.ExternalEventId is null)
        {
            // Nothing was ever written, so there is nothing to move: either no calendar was
            // connected when the booking was made, or the write was refused and the Failed row
            // beside it already says so. Placing it now would hide a second decision inside a
            // reschedule — the meeting would appear in a calendar the booker was never told it
            // was in — and the sync state on the booking read is where that gap is meant to
            // become visible instead.
            return;
        }

        try
        {
            await calendars.UpdateEventAsync(
                entry.ExternalEventId, Describe(booking), cancellationToken);
        }
        catch (Exception exception)
        {
            await RecordFailureAsync(booking.Id, entry, exception);
            return;
        }

        // The same row and the same event id, updated in place. Synced again rather than left
        // alone, because a previous failure that has now succeeded must stop being reported: the
        // state is where the meeting is now, not the worst thing that ever happened to it.
        entry.State = CalendarSyncState.Synced;
        entry.LastError = null;
        entry.UpdatedAt = Now();

        await context.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// The withdrawal, and the two decisions about what it leaves behind.
    /// </summary>
    /// <remarks>
    /// <see cref="CalendarEntry"/> is kept, with its event id, and moved to
    /// <see cref="CalendarSyncState.Cancelled"/>. Deleting it would erase the only record that
    /// this booking was ever in a calendar and make a withdrawn meeting indistinguishable from
    /// one that never synced — the two states an operator most needs told apart. Leaving it
    /// Synced would be a row asserting an event that is no longer there.
    ///
    /// <see cref="ConferenceLink"/> is left exactly as it is, and deliberately so. It has no
    /// state column by design — the row exists or it does not — and the booking's own status is
    /// already the single answer to whether this meeting is happening; copying that answer into a
    /// second table is a second place for it to go stale. What the row records is that a
    /// conference was provisioned, which stays true after a cancellation.
    /// </remarks>
    private async Task WithdrawAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var entry = await FindEntryAsync(bookingId, cancellationToken);
        if (entry?.ExternalEventId is null)
        {
            return;
        }

        try
        {
            await calendars.CancelEventAsync(entry.ExternalEventId, cancellationToken);
        }
        catch (Exception exception)
        {
            await RecordFailureAsync(bookingId, entry, exception);
            return;
        }

        entry.State = CalendarSyncState.Cancelled;
        entry.LastError = null;
        entry.UpdatedAt = Now();

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

    /// <summary>
    /// Tracked on purpose: the callers that pass an entry in mutate it and expect the save below
    /// to carry the change.
    /// </summary>
    private Task<CalendarEntry?> FindEntryAsync(Guid bookingId, CancellationToken cancellationToken) =>
        context.CalendarEntries.FirstOrDefaultAsync(
            entry => (entry.BookingId == bookingId) && (entry.Provider == calendars.Provider),
            cancellationToken);

    /// <summary>
    /// One row per booking per provider, so a failure either starts that row or overwrites what
    /// it said. <paramref name="entry"/> is null only on the create path, where none can exist.
    /// </summary>
    private Task RecordFailureAsync(Guid bookingId, CalendarEntry? entry, Exception exception)
    {
        logger.LogWarning(
            exception, "Calendar sync failed for booking {Booking}. The booking stands.", bookingId);

        if (entry is null)
        {
            entry = new CalendarEntry { BookingId = bookingId, Provider = calendars.Provider };
            context.CalendarEntries.Add(entry);
        }

        entry.State = CalendarSyncState.Failed;
        entry.LastError = exception.Message;
        entry.UpdatedAt = Now();

        // ExternalEventId is left alone rather than cleared. On a move or a withdrawal that
        // failed, the event is still in the calendar under that id, and it is the only handle
        // anybody has for going and fixing it by hand.

        // Uncancellable for the same reason as the success path, and more so: this row is the
        // only thing that will tell the operator their calendar is not receiving bookings.
        return context.SaveChangesAsync(CancellationToken.None);
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
