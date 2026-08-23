using System.Security.Cryptography;

using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;

using NodaTime;

namespace Kairos.Api.Features.Bookings;

/// <summary>
/// A hold's life outside of an answer: minted here, closed here when something happens to the
/// meeting under it. One place, because a token minted two ways is hashed two ways, and a hold left
/// open by one release path is the defect that produced <see cref="WithdrawOpenAsync"/>.
/// </summary>
/// <remarks>
/// It adds and does not save, <see cref="Integrations.Webhooks.OutboxWriter"/>'s contract: the
/// caller decides what commits with it. The plaintext is returned rather than stored — only its
/// SHA-256 reaches the database, so a leak hands nobody the power to answer for an invitee.
/// </remarks>
public sealed class ProposalWriter(KairosDbContext context, IClock clock)
{
    private const int TokenBytes = 32;
    private const int SecondsPerHour = 60 * 60;

    /// <summary>
    /// A hold's state as of <paramref name="now"/>: the fold every reader of one has to apply.
    /// </summary>
    /// <remarks>
    /// A hold whose expiry has passed has stopped holding, whether or not the sweeper has marked it
    /// <see cref="ProposalState.Expired"/> — that service mails the operator about a lapse, it does
    /// not decide one happened. Every surface draws its controls off this answer, and two copies of
    /// the fold would drift into one offering to close a hold the other has given up on. Static
    /// because nothing about it needs the database or a clock of its own.
    /// </remarks>
    public static ProposalState StateAt(ProposalState state, long expiresAt, Instant now) =>
        (state == ProposalState.Open) && (expiresAt <= now.ToUnixTimeSeconds())
            ? ProposalState.Expired
            : state;

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
    /// Every other reader treats a lapsed hold as holding nothing without help: <see cref="StateAt"/>
    /// folds the clock, and both conflict readers require <see cref="BookingProposal.ExpiresAt"/> to
    /// be in the future before the row blocks anything. The propose route cannot stop at reading,
    /// because the filtered unique index over BookingId WHERE State = Open is a ROW, not a predicate
    /// a query can fold a clock into: a lapsed row left Open occupies the slot the next suggestion
    /// needs, and the operator cannot re-propose until something closes it. This is that something.
    ///
    /// <see cref="ProposalState.Expired"/> rather than <see cref="ProposalState.Withdrawn"/>: nobody
    /// took this back, the invitee ran out of time, and those are different things to tell an
    /// operator. <see cref="BookingProposal.NotifiedAt"/> is left null so the sweeper, whose
    /// predicate is Expired rows nobody has been told about, still sends the notice.
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
    /// Withdrawn rather than answered: nobody said no, the question stopped applying. Left Open, the
    /// row keeps the slot blocked in both conflict readers for the rest of the window and the token
    /// still resolves, so a later accept moves a meeting that is off, or moves one a second time
    /// from a start the invitee never saw.
    ///
    /// <paramref name="now"/> is the caller's reading: this is stamped into the same save as the
    /// status change beside it, and two clock reads in one transaction are two answers.
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
