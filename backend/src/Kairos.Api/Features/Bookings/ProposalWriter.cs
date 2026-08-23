using System.Security.Cryptography;

using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;

using NodaTime;

namespace Kairos.Api.Features.Bookings;

/// <summary>
/// A hold's life outside of an answer: minted here, and closed here when something happens to the
/// meeting under it. One place, because a token minted two ways is a token hashed two ways, and
/// because a hold left open by one release path and closed by another is the defect that produced
/// this method.
/// </summary>
/// <remarks>
/// It adds and it does not save, the same contract <see cref="Integrations.Webhooks.OutboxWriter"/>
/// keeps and for a related reason: the caller decides what commits with it. The plaintext is
/// returned rather than stored — only its SHA-256 reaches the database, exactly as a manage token
/// does, so a database that leaks does not hand anybody the power to answer for an invitee.
/// </remarks>
public sealed class ProposalWriter(KairosDbContext context, IClock clock)
{
    private const int TokenBytes = 32;
    private const int SecondsPerHour = 60 * 60;

    /// <summary>
    /// A hold's state as of <paramref name="now"/>: the fold every reader of one has to apply.
    /// </summary>
    /// <remarks>
    /// A hold whose expiry has passed has stopped holding, whether or not the sweeper has been
    /// round to mark it <see cref="ProposalState.Expired"/> — that service mails the operator
    /// about a lapse, it does not decide that one happened. Every surface draws
    /// controls off this answer: the invitee's page puts Accept and Decline over an open one, the
    /// operator's row puts Withdraw there. Two copies of the fold would drift into one of them
    /// offering to close a hold the other has already given up on, so there is one, and it is
    /// static because nothing about it needs the database or a clock of its own.
    /// </remarks>
    public static ProposalState StateAt(ProposalState state, long expiresAt, Instant now) =>
        (state == ProposalState.Open) && (expiresAt <= now.ToUnixTimeSeconds())
            ? ProposalState.Expired
            : state;

    /// <summary>Adds the proposal and returns it with the plaintext token beside it.</summary>
    public (BookingProposal Proposal, string Token) Add(Booking booking, Instant start, int holdHours)
    {
        var now = clock.GetCurrentInstant().ToUnixTimeSeconds();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenBytes)).ToLowerInvariant();

        var proposal = new BookingProposal
        {
            Id = Guid.CreateVersion7(),
            BookingId = booking.Id,
            StartUtc = start.ToUnixTimeSeconds(),

            // The booking's own length, never a length from the request. A different duration is a
            // different meeting, which is the reasoning CreateBookingRequest already carries.
            EndUtc = start.ToUnixTimeSeconds() + (booking.EndUtc - booking.StartUtc),
            ExpiresAt = now + ((long)holdHours * SecondsPerHour),
            CreatedAt = now,
            State = ProposalState.Open,
            TokenHash = BookingWriter.Hash(token),
        };

        context.BookingProposals.Add(proposal);
        return (proposal, token);
    }

    /// <summary>
    /// Closes the meeting's hold if it has already lapsed, without saving — so it commits with
    /// whatever the caller is already writing.
    /// </summary>
    /// <remarks>
    /// Every other reader of a hold treats a lapsed one as holding nothing and needs no help doing
    /// it: <see cref="StateAt"/> folds the clock, and both conflict readers require
    /// <see cref="BookingProposal.ExpiresAt"/> to be in the future before the row blocks anything.
    /// The propose route is the one place that cannot stop at reading, and the reason is the
    /// filtered unique index over BookingId WHERE State = Open — that is a ROW, not a predicate a
    /// query can fold a clock into. A lapsed row left Open goes on occupying the one slot the next
    /// suggestion needs, so the operator cannot re-propose until something closes it. This is what
    /// closes it, and it is why re-proposing no longer waits on the sweeper the way availability
    /// never did.
    ///
    /// <see cref="ProposalState.Expired"/> rather than <see cref="ProposalState.Withdrawn"/>:
    /// nobody took this back, the invitee ran out of time, and those are different things to tell
    /// an operator. <see cref="BookingProposal.NotifiedAt"/> is deliberately left null — the
    /// sweeper's own predicate is Expired rows nobody has been told about, so closing one here
    /// hands the notice to it rather than swallowing it.
    /// </remarks>
    public async Task ExpireLapsedAsync(
        Guid bookingId,
        Instant now,
        CancellationToken cancellationToken)
    {
        // Hoisted, because NodaTime's conversion does not translate inside an expression tree.
        var seconds = now.ToUnixTimeSeconds();

        var lapsed = await context.BookingProposals
            .FirstOrDefaultAsync(
                proposal => (proposal.BookingId == bookingId)
                    && (proposal.State == ProposalState.Open)
                    && (proposal.ExpiresAt <= seconds),
                cancellationToken);

        if (lapsed is null)
        {
            return;
        }

        lapsed.State = ProposalState.Expired;
        lapsed.DecidedAt = seconds;
    }

    /// <summary>
    /// Closes the meeting's open hold, if it has one, without saving — so it commits with whatever
    /// the caller is already writing.
    /// </summary>
    /// <remarks>
    /// Withdrawn rather than answered: nobody said no, the question stopped applying. Both callers
    /// are releases of the meeting underneath — the cancel and the invitee's own reschedule — and
    /// leaving the row Open after either is expensive twice over. The slot stays blocked in both
    /// conflict readers for the rest of the window, and the token still resolves, so an accept
    /// arriving afterwards moves a meeting that is off, or moves one a second time away from a
    /// current time the invitee never saw.
    ///
    /// <paramref name="now"/> is the caller's reading rather than a fresh one, because this is
    /// stamped into the same save as the status change beside it and two clock reads in one
    /// transaction are two answers to when it happened.
    /// </remarks>
    public async Task WithdrawOpenAsync(Guid bookingId, Instant now, CancellationToken cancellationToken)
    {
        var open = await context.BookingProposals
            .FirstOrDefaultAsync(
                proposal => (proposal.BookingId == bookingId) && (proposal.State == ProposalState.Open),
                cancellationToken);

        if (open is null)
        {
            return;
        }

        open.State = ProposalState.Withdrawn;
        open.DecidedAt = now.ToUnixTimeSeconds();
    }
}
