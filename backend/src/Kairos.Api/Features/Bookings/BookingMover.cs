using Kairos.Api.Availability;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Shared;

using Microsoft.EntityFrameworkCore;

using NodaTime;
using NodaTime.Text;

namespace Kairos.Api.Features.Bookings;

/// <summary>
/// Where a booking sits in time, and every rule about putting it there.
/// </summary>
/// <remarks>
/// The reschedule, the propose and the accept all run the same dance: release the booking, ask
/// whether the slot is offered with it out of the way, then put it back. Written out three times,
/// the copies had begun to differ — the reschedule closed an open proposal and the accept did not,
/// which is a slot blocked for two days and a token still answering for a meeting that has moved.
///
/// The release is the part that has to be one copy. A confirmed booking blocks its own calendar,
/// so nothing can check whether it may move to 11:30 while it still sits at 11:00, and every path
/// out of the released state has to put it back, the failing ones included.
/// </remarks>
public sealed class BookingMover(KairosDbContext context, SlotQuery slots)
{
    private const int SecondsPerMinute = 60;

    /// <summary>
    /// The instant a request named, or a 400 saying what one looks like. Here rather than beside
    /// the request records so that the two routes binding a start string and the one path already
    /// holding an <see cref="Instant"/> agree about what a start is.
    /// </summary>
    public static Instant ParseInstant(string value)
    {
        var parsed = InstantPattern.ExtendedIso.Parse(value);
        if (!parsed.Success)
        {
            throw DomainException.Invalid(
                BookingsEndpoints.InstantInvalidCode,
                "'start' must be a UTC instant, such as 2026-09-07T09:00:00Z.");
        }

        return parsed.Value;
    }

    /// <summary>
    /// A meeting that is off does not move. Checked before the release rather than after, or the
    /// answer would be produced by cancelling an already-cancelled booking.
    /// </summary>
    public static void RequireMovable(Booking booking)
    {
        if (booking.Status != BookingStatus.Confirmed)
        {
            throw DomainException.Conflict(
                BookingsEndpoints.BookingCancelledCode, "A cancelled booking cannot be moved.");
        }
    }

    /// <summary>
    /// Availability, minimum notice and the daily cap enforced on write. <see cref="BookingWriter"/>
    /// only refuses a clash with another confirmed booking, so without this a POST naming 03:00 on
    /// a Sunday would succeed against a calendar that never offered it. Returns what the query
    /// resolved, sparing the create path a second resolution of the link and its margin.
    /// </summary>
    public async Task<SlotResults> RequireOfferedAsync(
        string slug,
        int durationMinutes,
        Instant start,
        Instant now,
        CancellationToken cancellationToken)
    {
        var zone = DateTimeZoneProviders.Tzdb[await TimeZoneIdAsync(cancellationToken)];
        var date = start.InZone(zone).Date;
        var offered = await slots.GetSlotsAsync(slug, durationMinutes, date, date, now, cancellationToken);
        if (!offered.Starts.Contains(start))
        {
            throw DomainException.Conflict(
                BookingsEndpoints.SlotUnavailableCode, "That time is not available.");
        }

        return offered;
    }

    /// <summary>
    /// Releases the booking, asks whether <paramref name="start"/> is offered with it out of the
    /// way, and returns with it still released, so the caller stages whatever else belongs in the
    /// save that puts it back. Every other path out of here restores it to confirmed first.
    /// </summary>
    /// <param name="compensate">
    /// Run when, and only when, the check REFUSES. The accept closes the proposal it was answering,
    /// whose slot is gone; the other callers pass null.
    /// </param>
    public async Task ReleaseAndCheckAsync(
        Booking booking,
        Instant start,
        Instant now,
        Func<Task>? compensate,
        CancellationToken cancellationToken)
    {
        // KNOWN HOLE, not fixed here. Between this save and whichever restore follows, the booking
        // is Cancelled on disk with nobody having cancelled it, and BookingCanceller reads that as
        // "off already" and does nothing: a cancel landing in the window answers 200 and is undone,
        // leaving the meeting standing with nothing announced, no event withdrawn, nobody told.
        // Closing it needs a release distinguishable from a cancellation — a flag, or a re-read
        // under the write lock — a change with its own tests. This class buys the window one
        // description instead of three.
        booking.Status = BookingStatus.Cancelled;
        booking.CancelledAt = now.ToUnixTimeSeconds();
        await context.SaveChangesAsync(cancellationToken);

        try
        {
            await RequireOfferedAsync(
                booking.Link.Slug, DurationMinutes(booking), start, now, cancellationToken);
        }
        catch (Exception exception)
        {
            // Restored for every failure, a dropped connection included — that is what makes the
            // release safe to commit at all. Compensated only for a refusal, because the
            // compensation writes a DECISION and only DomainException means the check gave one. A
            // provider that fell over, or an invitee who closed the tab mid-request, is not that
            // invitee having lost their slot: closing a live proposal over it costs them a
            // suggestion they never saw and cannot get back.
            if (exception is DomainException && compensate is not null)
            {
                await compensate();
            }

            await ConfirmAsync(booking);
            throw;
        }
    }

    /// <summary>
    /// Puts the booking back confirmed at a new instant, carrying whatever else the caller staged.
    /// </summary>
    /// <remarks>
    /// The row is mutated rather than replaced: the identifier and the manage token belong to the
    /// meeting, not the slot. Sequence is how the .ics says "same event, new time" instead of a
    /// second invitation, and the length is carried over — a different duration is a different
    /// meeting.
    /// </remarks>
    /// <param name="now">
    /// The caller's reading rather than a fresh one, so the release, the availability check and
    /// the reminder below all answer the same instant.
    /// </param>
    public async Task MoveToAsync(Booking booking, Instant start, Instant now)
    {
        var seconds = booking.EndUtc - booking.StartUtc;
        booking.StartUtc = start.ToUnixTimeSeconds();
        booking.EndUtc = booking.StartUtc + seconds;
        booking.Sequence++;

        // Here because this is the only place StartUtc changes — the reason Sequence is advanced
        // here too. A reminder is a copy of the meeting's time, and this is where the original
        // stops matching it. Deleted and rewritten rather than updated in place, which makes
        // moved-later, moved-inside-the-lead and moved-back-out one path instead of three; a row
        // already sent goes with the rest, and the reschedule mail naming the new hour is a better
        // notice than a second "tomorrow". Staged, so it rides the save below and cannot commit
        // apart from the move it belongs to.
        //
        // The read is uncancellable for ConfirmAsync's reason: by now the booking is Cancelled on
        // disk, and a token firing here would leave it that way with nobody having cancelled it.
        var existing = await context.Reminders
            .SingleOrDefaultAsync(reminder => reminder.BookingId == booking.Id, CancellationToken.None);

        // Carried across the delete rather than re-encrypted from a plaintext this method could
        // ask for, because one of its callers cannot supply one: accepting a proposal arrives
        // holding the PROPOSAL's token, and the manage token exists in the database only as a
        // SHA-256. The ciphertext is already here and it is the same token, so the meeting keeps
        // its manage link however it moved. Read before the Remove below, so the order of these
        // two statements is never what decides it.
        //
        // Null when there was no row to read: a booking made inside the lead schedules nothing, so
        // moving it back out writes its first reminder here, with no secret and no way to get one.
        // That reminder loses its manage link permanently. Degraded rather than broken, and the
        // same shape the operator's own cancellation notice already has.
        var secret = existing?.Secret;

        if (existing is not null)
        {
            context.Reminders.Remove(existing);
        }

        if (ReminderSchedule.ShouldSchedule(start, now))
        {
            context.Reminders.Add(new Reminder
            {
                BookingId = booking.Id,
                DueAt = ReminderSchedule.DueAt(start).ToUnixTimeSeconds(),
                Secret = secret,
            });
        }

        await ConfirmAsync(booking);
    }

    public Task RestoreAsync(Booking booking) => ConfirmAsync(booking);

    /// <summary>
    /// Deliberately uncancellable, and the reason releasing at all is safe: it compensates a write
    /// this request has already made, so it outlives the request as
    /// <see cref="Integrations.Google.GoogleClientFactory"/>'s reconnect flag does. A dropped
    /// connection between the release and this would otherwise leave the booking cancelled forever,
    /// with no event withdrawn and nothing announced — both live in the cancel path, which never
    /// ran.
    /// </summary>
    private Task ConfirmAsync(Booking booking)
    {
        booking.Status = BookingStatus.Confirmed;
        booking.CancelledAt = null;
        return context.SaveChangesAsync(CancellationToken.None);
    }

    private static int DurationMinutes(Booking booking) =>
        (int)((booking.EndUtc - booking.StartUtc) / SecondsPerMinute);

    private Task<string> TimeZoneIdAsync(CancellationToken cancellationToken) =>
        context.Settings.AsNoTracking()
            .Where(row => row.Id == Settings.SingletonId)
            .Select(row => row.TimeZoneId)
            .SingleAsync(cancellationToken);
}
