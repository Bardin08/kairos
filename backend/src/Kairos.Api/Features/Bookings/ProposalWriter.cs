using System.Security.Cryptography;

using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using NodaTime;

namespace Kairos.Api.Features.Bookings;

/// <summary>
/// Mints a proposal and the token the invitee decides it with. One place, because a token minted
/// two ways is a token hashed two ways.
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
}
