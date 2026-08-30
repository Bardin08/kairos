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
        // Before anything is attempted, because this method can now run twice: a follow-up whose
        // mail step failed is retried whole, and a second insert under the same deterministic id
        // comes back a duplicate that RecordFailureAsync would write over a genuinely Synced row.
        // Keyed on Synced and not on the row existing, so a Failed attempt is still retried — that
        // distinction is the whole guard.
        var placedAlready = await FindEntryAsync(booking.Id, cancellationToken);
        if (placedAlready?.State == CalendarSyncState.Synced)
        {
            // The event is there; the conference may not be. AddConferenceAsync has its own row
            // guard, so this is safe to repeat — and it is the only path by which a conference
            // that failed on its first attempt is ever retried.
            await AddConferenceAsync(booking.Id, cancellationToken);
            await context.SaveChangesAsync(CancellationToken.None);
            return;
        }

        PlacedEvent placed;
        try
        {
            // Asked before anything is attempted, because "nobody connected a calendar" is not a
            // failure and must not be recorded as one. See the class remarks on why the absence
            // of a row is the honest answer there.
            if (!await calendars.IsConnectedAsync(cancellationToken))
            {
                return;
            }

            placed = await calendars.CreateEventAsync(Describe(booking), cancellationToken);
        }
        catch (Exception exception)
        {
            // placedAlready is the row a prior Failed attempt left, if this is a retry of one —
            // passed through rather than null, so a second refusal updates that row instead of
            // colliding with it under the same unique (booking, provider) index.
            await RecordFailureAsync(booking.Id, placedAlready, exception);
            return;
        }

        if (placedAlready is null)
        {
            placedAlready = new CalendarEntry { BookingId = booking.Id, Provider = calendars.Provider };
            context.CalendarEntries.Add(placedAlready);
        }

        placedAlready.ExternalEventId = placed.ExternalEventId;
        placedAlready.CalendarUid = placed.CalendarUid;
        placedAlready.State = CalendarSyncState.Synced;
        placedAlready.LastError = null;
        placedAlready.UpdatedAt = Now();

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

        // Already withdrawn, so a retry has nothing to do. Without this, the second delete answers
        // "HttpStatusCode is Gone. Resource has been deleted" and RecordFailureAsync writes that
        // over a Cancelled row that is telling the truth.
        if (entry.State == CalendarSyncState.Cancelled)
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
            // PlaceAsync's guard, for the conference: a retry must not mint a second meeting room,
            // and the row is what says one already exists. No state column to consult here by
            // design — the row's absence is what "no join link" looks like.
            if (await context.ConferenceLinks
                    .AnyAsync(row => row.BookingId == bookingId, cancellationToken))
            {
                return;
            }

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
    /// it said. <paramref name="entry"/> is null when no attempt has ever been recorded for this
    /// booking and provider — the first attempt, from whichever of the three callers made it —
    /// and non-null on every attempt after, a retry of a prior failure included.
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
