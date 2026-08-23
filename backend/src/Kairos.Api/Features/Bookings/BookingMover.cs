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
/// Four paths decide a booking's instant — the create, the invitee's reschedule, the operator's
/// propose and the invitee's accept — and three of them are the same delicate dance: release the
/// booking, ask whether the slot is offered with it out of the way, then put it back. That dance
/// was written out three times, and the copies had already begun to differ: the reschedule learned
/// to close an open proposal and the accept did not, which is a slot blocked for two days and a
/// token that still answers for a meeting that has already moved. It is exactly the argument that
/// produced <see cref="BookingCanceller"/>, and it gets the same answer.
///
/// The release is the part that has to be one copy. A confirmed booking blocks its own calendar,
/// so nothing can check whether it may move to 11:30 while it is still sitting at 11:00 — and
/// every path out of the released state has to put it back, the failing ones included.
/// </remarks>
public sealed class BookingMover(KairosDbContext context, SlotQuery slots)
{
    private const int SecondsPerMinute = 60;

    /// <summary>
    /// The instant a request named, or a 400 saying what one looks like. Static and here rather
    /// than beside the request records, because the two routes that bind a start string and the
    /// one path that already holds an <see cref="Instant"/> must agree about what a start is.
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
    /// only refuses a clash with another confirmed booking, so without this a POST naming 03:00
    /// on a Sunday would succeed against a calendar that never offered it. Returns what the query
    /// resolved, so the create path never has to resolve the link or its margin a second time.
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
    /// way, and returns with the booking still released — so the caller stages whatever else
    /// belongs in the save that puts it back.
    /// </summary>
    /// <param name="compensate">
    /// Run when, and only when, the check REFUSES. The accept uses it to close the proposal it was
    /// answering: the slot that proposal was holding is gone, so re-offering it would be offering
    /// something that is not there. The other callers have nothing to put there and pass null.
    /// </param>
    /// <remarks>
    /// Every path out of here leaves the booking confirmed except the successful one, which leaves
    /// it released on purpose and hands the caller the job of re-placing it.
    /// </remarks>
    public async Task ReleaseAndCheckAsync(
        Booking booking,
        Instant start,
        Instant now,
        Func<Task>? compensate,
        CancellationToken cancellationToken)
    {
        // KNOWN HOLE, and it is not fixed here. Between this save and whichever restore follows,
        // the booking is Cancelled on disk while nobody has cancelled it, and
        // <see cref="BookingCanceller"/> reads that as "off already" and returns having done
        // nothing. A cancel landing in the window therefore answers 200 and is then undone: the
        // meeting stands, nothing was announced, no calendar event was withdrawn and nobody was
        // told. Closing it means a release distinguishable from a cancellation — a flag, or a
        // re-read under the write lock — and that is a change with its own tests. What this class
        // does buy is that the window has one description instead of three.
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
            // release safe to commit at all. Compensated only for a refusal, and the two are not
            // the same question: the compensation writes a DECISION, and only DomainException
            // means the check gave one. A provider that fell over, or an invitee who closed the
            // tab mid-request, is not that invitee having lost their slot — closing a live
            // proposal over it costs them a suggestion they never saw and cannot get back.
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
    /// meeting, not to the slot. Sequence is how the .ics tells a calendar client that this is the
    /// same event at a new time instead of a second invitation, and its length is carried over
    /// rather than recomputed — a different duration is a different meeting.
    /// </remarks>
    public Task MoveToAsync(Booking booking, Instant start)
    {
        var seconds = booking.EndUtc - booking.StartUtc;
        booking.StartUtc = start.ToUnixTimeSeconds();
        booking.EndUtc = booking.StartUtc + seconds;
        booking.Sequence++;
        return ConfirmAsync(booking);
    }

    /// <summary>Puts the booking back confirmed exactly where it was, carrying the caller's stage.</summary>
    public Task RestoreAsync(Booking booking) => ConfirmAsync(booking);

    /// <summary>
    /// Deliberately uncancellable, and the reason releasing at all is safe. It is the compensation
    /// for a write this request has already made, so it has to outlive the request the same way
    /// <see cref="Integrations.Google.GoogleClientFactory"/>'s reconnect flag does: a caller whose
    /// connection drops in the window between the release and this would otherwise leave the
    /// booking cancelled forever — reads reporting a meeting that is off, every move refusing it,
    /// and nothing having withdrawn the calendar event or announced anything, because both of
    /// those live in the cancel path and never ran.
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
