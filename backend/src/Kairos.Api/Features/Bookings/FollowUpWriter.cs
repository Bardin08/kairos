using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Security;

using NodaTime;

namespace Kairos.Api.Features.Bookings;

/// <remarks>
/// It adds and it never saves — <see cref="Integrations.Webhooks.OutboxWriter"/>'s discipline, for
/// its reason: the row and the change it describes commit together or neither does. A writer
/// saving on its own behalf would reintroduce the dual write, and here that is a booking whose
/// booker is never told or a telling about a booking that was never written.
/// </remarks>
public sealed class FollowUpWriter(KairosDbContext context, SecretProtector protector, IClock clock)
{
    /// <summary>
    /// Adds the row and returns it, tracked and unsaved. The return value exists for the one
    /// caller that has to take it back: <see cref="BookingWriter"/> enqueues inside its own
    /// transaction, and a row left in the change tracker after a failure there would ride along on
    /// the next SaveChanges and mail a booking that does not exist.
    /// </summary>
    /// <param name="manageToken">
    /// Plaintext. Encrypted here and nowhere else, so no caller has to know that the database
    /// holds only a hash of it. Null when the caller has none, and null is stored — the ciphertext
    /// of an empty string would decrypt happily and print an empty manage link.
    /// </param>
    public FollowUp Enqueue(
        Guid bookingId,
        FollowUpType type,
        string? manageToken = null,
        Instant? previousStart = null)
    {
        var followUp = new FollowUp
        {
            BookingId = bookingId,
            Type = type,
            Secret = manageToken is null ? null : protector.Protect(manageToken),
            PreviousStartUtc = previousStart?.ToUnixTimeSeconds(),
            CreatedAt = clock.GetCurrentInstant().ToUnixTimeSeconds(),
        };

        context.FollowUps.Add(followUp);
        return followUp;
    }
}
