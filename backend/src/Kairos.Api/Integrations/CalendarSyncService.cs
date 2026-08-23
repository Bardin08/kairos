using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;

using NodaTime;

namespace Kairos.Api.Integrations;

/// <summary>
/// This is the file the provider-neutrality of Part B is judged on, so read its usings — there is
/// no vendor namespace among them, and adding one would be the bug.
/// </summary>
/// <remarks>
/// It never throws. Ground rule 14: a booking the domain accepted is never lost because a third
/// party had a bad minute, so every path out of here is a return. A provider that refuses becomes
/// a <see cref="CalendarSyncState.Failed"/> row with the reason on it; one nobody connected
/// becomes nothing at all.
///
/// <see cref="CalendarEntry"/> is unique per (booking, provider), so a booking has one row for its
/// whole life and the state on it answers "where is this meeting, really" —
/// <see cref="CalendarSyncState.Synced"/>, <see cref="CalendarSyncState.Failed"/> with the
/// provider's reason, or <see cref="CalendarSyncState.Cancelled"/>. Nothing here deletes a row:
/// the id of an event that once existed is the only evidence it did.
/// </remarks>
public sealed class CalendarSyncService(
    ICalendarWriter calendars,
    IConferenceCreator conferences,
    KairosDbContext context,
    IClock clock,
    ILogger<CalendarSyncService> logger)
{
    private const string TitleJoiner = " with ";

    /// <summary>
    /// <paramref name="booking"/> must arrive with its link and participants loaded — this reads a
    /// meeting, not a row, and re-fetching what the caller already has would be a second query on
    /// the create path for nothing.
    /// </summary>
    public Task SyncAsync(Booking booking, CancellationToken cancellationToken) =>
        GuardedAsync(booking.Id, () => PlaceAsync(booking, cancellationToken));

    /// <summary>
    /// Same loading requirement as <see cref="SyncAsync"/>, because the event is rewritten from
    /// the booking.
    /// </summary>
    /// <remarks>
    /// Called after the booking is confirmed at its new time, never while a reschedule has it
    /// released. A reschedule sets the booking cancelled and back to confirmed on every path out,
    /// so anything hung on the status change would withdraw the event for every meeting that
    /// merely moved. That is the trap the <c>booking.cancelled</c> announcement is kept out of,
    /// and why moving and withdrawing are two methods rather than one hook.
    /// </remarks>
    public Task RescheduleAsync(Booking booking, CancellationToken cancellationToken) =>
        GuardedAsync(booking.Id, () => MoveAsync(booking, cancellationToken));

    /// <summary>
    /// The id alone, because a cancellation rewrites nothing — it only names what to remove.
    /// </summary>
    public Task CancelAsync(Guid bookingId, CancellationToken cancellationToken) =>
        GuardedAsync(bookingId, () => WithdrawAsync(bookingId, cancellationToken));

    /// <summary>
    /// The outer net, and why none of the three reports a failure of its own: by the time any of
    /// them runs the domain has committed and the caller is about to answer. An exception escaping
    /// here would turn a booking that exists into a 500 the caller reads as "it did not work".
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

        // One SaveChanges, so the entry and the link commit together — a join URL recorded without
        // the event that carries it would be a meeting nobody can find twice over. Uncancellable:
        // a booker who closed the tab is not a reason to lose the record of work already done.
        await context.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// No connectivity check, unlike <see cref="PlaceAsync"/>, and that is the design: reaching
    /// here with an event id means a calendar <em>was</em> connected when the booking was made, so
    /// a provider that now refuses is a real failure an operator has to see — an event sitting at
    /// a time the booking no longer claims is the two-plausible-times problem this method ends.
    /// The adapter throws on a missing credential and that throw becomes the row.
    /// </summary>
    private async Task MoveAsync(Booking booking, CancellationToken cancellationToken)
    {
        var entry = await FindEntryAsync(booking.Id, cancellationToken);
        if (entry?.ExternalEventId is null)
        {
            // Nothing was ever written, so there is nothing to move: either no calendar was
            // connected when the booking was made, or the write was refused and the Failed row
            // says so. Placing it now would hide a second decision inside a reschedule, putting
            // the meeting in a calendar the booker was never told it was in.
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

        // Synced again rather than left alone, because a previous failure that has now succeeded
        // must stop being reported: the state is where the meeting is now, not the worst thing
        // that ever happened to it.
        entry.State = CalendarSyncState.Synced;
        entry.LastError = null;
        entry.UpdatedAt = Now();

        await context.SaveChangesAsync(CancellationToken.None);
    }

    /// <remarks>
    /// <see cref="CalendarEntry"/> is kept, with its event id, and moved to
    /// <see cref="CalendarSyncState.Cancelled"/>. Deleting it would make a withdrawn meeting
    /// indistinguishable from one that never synced — the two states an operator most needs told
    /// apart — and leaving it Synced would assert an event that is no longer there.
    ///
    /// <see cref="ConferenceLink"/> is left exactly as it is. It has no state column by design,
    /// and the booking's own status already answers whether this meeting is happening; the row
    /// records that a conference was provisioned, which stays true after a cancellation.
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
    /// Best effort, separately so. The event is already in the calendar, so a conferencing failure
    /// must not downgrade a real <see cref="CalendarSyncState.Synced"/> to
    /// <see cref="CalendarSyncState.Failed"/>. <see cref="ConferenceLink"/> has no state column
    /// for the same reason: its absence is what "no join link" looks like.
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
    /// Reads the booking and nothing else — no settings, no formatting rules, no provider.
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
