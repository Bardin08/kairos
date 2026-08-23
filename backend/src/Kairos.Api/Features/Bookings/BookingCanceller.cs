using Kairos.Api.Integrations;
using Kairos.Api.Integrations.Email;
using Kairos.Api.Integrations.Webhooks;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;

using NodaTime;

namespace Kairos.Api.Features.Bookings;

/// <summary>
/// Calling a meeting off, once and in one place, whoever asked for it.
/// </summary>
/// <remarks>
/// There are two principals who may cancel and there is one cancellation. The invitee opens their
/// manage link; the operator presses a button in the workbench. What differs between them is who
/// they are, whether they gave a reason and whether a plaintext manage token is in hand — three
/// arguments. What does not differ is the status write, the sequence, the announcement, the
/// contention rule, the calendar withdrawal and the mail, and those are the parts where a second
/// copy silently stops matching the first.
///
/// The <c>booking.cancelled</c> announcement is enqueued here and deliberately not wherever
/// <see cref="Booking.Status"/> becomes <see cref="BookingStatus.Cancelled"/>.
/// <see cref="BookingsEndpoints"/>'s reschedule is release, re-check, re-place: it sets a booking
/// cancelled and back to confirmed on every path out, so a hook on the status change would
/// announce a cancellation for every meeting that merely moved. This is the only place where
/// cancelled is where the booking stays.
/// </remarks>
public sealed class BookingCanceller(
    KairosDbContext context,
    OutboxWriter outbox,
    CalendarSyncService calendars,
    BookingMailer mailer,
    IClock clock)
{
    /// <summary>
    /// Cancels <paramref name="booking"/>, or agrees that it is already off.
    /// </summary>
    /// <param name="reason">
    /// The operator's own words, printed in the invitee's cancellation. Null for a cancellation
    /// the invitee made themselves, and the null is the record of which of the two happened —
    /// which is why there is no second column saying who.
    /// </param>
    /// <param name="manageToken">
    /// The plaintext token, when the caller has one. The invitee does: they just opened a URL
    /// carrying it. The operator cannot: only the SHA-256 is stored, and it is never handed back
    /// after the 201 that minted it. The mail then goes with no manage link on it, which costs
    /// nothing — that link's only two uses are cancelling a booking already off and moving one
    /// that no longer exists.
    /// </param>
    /// <remarks>
    /// Idempotent. A manage link opened twice, or a double-clicked button, gets the answer the
    /// caller can already see rather than a 404 for a booking plainly in front of them or a 500
    /// out of the outbox's unique index. The early return and the catch below cover two different
    /// arrivals of that and neither substitutes for the other: the early return is for a caller
    /// who asks again after the first one finished, the catch for two callers whose saves are in
    /// flight together and who therefore both read the booking as confirmed.
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

        await CloseAnyHoldAsync(booking, now, cancellationToken);

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

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqliteErrors.IsUniqueViolation(exception))
        {
            // Two cancels in flight at once. The other one committed first, so the booking is off
            // and its announcement is queued; this call has nothing left to do but agree. Without
            // this catch, adding the outbox row would have turned an idempotent 200 into a 500.
            //
            // Returned from here rather than falling through, and that is load-bearing: the outbox
            // row this save could not write is still tracked, so the sync service's own
            // SaveChanges would raise the same violation again and lose the state change it was
            // making. The other call is withdrawing the event and sending the mail anyway.
            return;
        }

        // Ground rule 14, in the order it demands. The cancellation has committed and nothing
        // below can reach back and undo it: CancelAsync writes a Failed row rather than throwing,
        // and the mailer swallows a relay that is down. A provider that is unreachable must never
        // turn a cancel into a 500 for a meeting that is already off.
        await calendars.CancelAsync(booking.Id, cancellationToken);

        // After the withdrawal, so the message describes a calendar that has already let the slot
        // go. Whatever token the caller had stays in memory for this request and is never written
        // down — which is exactly why this runs here rather than behind the outbox, where a queued
        // row would have to carry the plaintext.
        await mailer.SendCancellationAsync(booking, manageToken, cancellationToken);
    }

    /// <summary>
    /// Closes an open proposal on the meeting, in the same save as the cancellation.
    /// </summary>
    /// <remarks>
    /// Not for availability: both conflict readers already ignore a hold whose booking is not
    /// confirmed, so the slot is back on offer the moment the cancellation commits whether or not
    /// this runs. It is for the sweeper. A proposal left Open on a cancelled meeting lapses on its
    /// own schedule and mails the operator that nobody answered their suggestion — about a meeting
    /// that is off, which is a message with no action behind it and no way for the reader to tell
    /// it is stale.
    /// </remarks>
    private async Task CloseAnyHoldAsync(Booking booking, Instant now, CancellationToken cancellationToken)
    {
        var open = await context.BookingProposals
            .FirstOrDefaultAsync(
                proposal => (proposal.BookingId == booking.Id) && (proposal.State == ProposalState.Open),
                cancellationToken);

        if (open is null)
        {
            return;
        }

        open.State = ProposalState.Withdrawn;
        open.DecidedAt = now.ToUnixTimeSeconds();
    }
}
