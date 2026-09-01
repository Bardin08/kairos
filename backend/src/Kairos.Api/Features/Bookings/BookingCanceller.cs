using Kairos.Api.Integrations.Webhooks;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;

using NodaTime;

namespace Kairos.Api.Features.Bookings;

/// <summary>
/// One cancellation for both principals. Invitee and operator differ only in who they are, their
/// reason and whether they hold a plaintext manage token; everything downstream is identical, and
/// a second copy of it stops matching silently.
/// </summary>
/// <remarks>
/// The <c>booking.cancelled</c> announcement is enqueued here rather than wherever
/// <see cref="Booking.Status"/> becomes <see cref="BookingStatus.Cancelled"/>:
/// <see cref="BookingsEndpoints"/>'s reschedule cancels and re-confirms on every path out, so a
/// hook on the status change would announce every meeting that merely moved. Here, cancelled is
/// where the booking stays.
/// </remarks>
public sealed class BookingCanceller(
    KairosDbContext context,
    OutboxWriter outbox,
    FollowUpWriter followUps,
    FollowUpSignal signal,
    ProposalWriter proposals,
    IClock clock)
{
    /// <summary>Cancels <paramref name="booking"/>, or agrees that it is already off.</summary>
    /// <param name="reason">
    /// The operator's words, printed in the invitee's cancellation. Null when the invitee cancelled,
    /// and that null is the record of which happened — hence no column naming the canceller.
    /// </param>
    /// <param name="manageToken">
    /// The plaintext token when the caller has one. The invitee does, having just opened a URL
    /// carrying it; the operator cannot, since only the SHA-256 is stored. That mail loses its
    /// manage link, which costs nothing: the link only cancels a booking already off.
    /// </param>
    /// <remarks>
    /// Idempotent. A manage link opened twice, or a double-clicked button, gets the answer the
    /// caller can already see rather than a 404 or a 500 out of the outbox's unique index. The early
    /// return covers asking again after the first finished; the catch covers two saves in flight.
    /// </remarks>
    public async Task CancelAsync(
        Booking booking,
        string? reason,
        string? manageToken,
        CancellationToken cancellationToken)
    {
        if (booking.Status != BookingStatus.Confirmed)
        {
            return;
        }

        var now = clock.GetCurrentInstant();
        booking.Status = BookingStatus.Cancelled;
        booking.CancelledAt = now.ToUnixTimeSeconds();
        booking.Sequence++;

        if (reason is not null)
        {
            booking.CancellationReason = reason;
        }

        // In the same save as the cancellation, so no window exists in which the meeting is off and
        // its hold is still holding. Not for availability — both readers ignore a hold whose booking
        // is not confirmed — but for the sweeper, which would otherwise lapse it and mail the
        // operator that nobody answered a suggestion about a cancelled meeting.
        await proposals.WithdrawOpenAsync(booking.Id, now, cancellationToken);

        // Here and NOT wherever Status becomes Cancelled, for the reason this class's own remarks
        // give about the announcement: reschedule is release, re-check, re-place, so every meeting
        // that merely moves passes through Cancelled. A hook on the status write would take the
        // reminder off at the release, and only BookingMover.MoveToAsync writes one back — so a
        // reschedule the re-check REFUSES would leave a meeting standing with nobody to remind,
        // and nothing would say so. Staged, so it rides the save below.
        var reminder = await context.Reminders
            .SingleOrDefaultAsync(candidate => candidate.BookingId == booking.Id, cancellationToken);

        if (reminder is not null)
        {
            context.Reminders.Remove(reminder);
        }

        // One SaveChanges, so the cancellation and the announcement of it commit together or
        // neither does — the same rule BookingWriter follows for creation, for the same reason.
        outbox.Enqueue(
            booking.Id,
            OutboxMessageType.BookingCancelled,
            BookingEventPayload.Cancelled(
                booking.Id,
                booking.Link.Slug,
                Instant.FromUnixTimeSeconds(booking.StartUtc),
                Instant.FromUnixTimeSeconds(booking.EndUtc)).ToJson());

        // Beside the announcement and for its reason: the calendar withdrawal and the mail are the
        // worker's now, and they must not run for a cancellation that turns out not to have
        // committed.
        followUps.Enqueue(booking.Id, FollowUpType.Cancelled, manageToken);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqliteErrors.IsUniqueViolation(exception))
        {
            // Two cancels in flight at once. The other committed first, so the booking is off and
            // its announcement queued; this call has nothing to do but agree, and without the catch
            // the outbox row would turn an idempotent 200 into a 500. Returning rather than falling
            // through is load-bearing: this save never committed, so the outbox and follow-up rows
            // it staged are only tracked, not written, and a nudge here would wake the worker for a
            // cancellation that did not happen. The other call already queued its own row and will
            // nudge for it.
            return;
        }

        signal.Nudge();
    }
}
