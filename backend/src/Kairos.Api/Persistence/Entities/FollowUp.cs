namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// Work a booking owes a third party — a calendar to write and a person to tell — written in the
/// same transaction as the booking itself and done off the request thread.
/// </summary>
/// <remarks>
/// Not an <see cref="OutboxMessage"/>, and the difference is the lifecycle rather than the shape.
/// An outbox message is an announcement a subscriber may want, marked delivered and kept for ever;
/// this carries a secret and is deleted the moment it is done. Confusing the two is how somebody
/// writes the wrong retry policy, which is why they have different names.
/// </remarks>
public sealed class FollowUp
{
    public Guid Id { get; set; }

    public Guid BookingId { get; set; }

    public Booking Booking { get; set; } = null!;

    public FollowUpType Type { get; set; }

    /// <summary>
    /// The manage token, encrypted by <see cref="Security.SecretProtector"/> — a queued notice has
    /// to be able to print the link, which is why this holds ciphertext rather than a hash the way
    /// <see cref="Booking.ManageTokenHash"/> does. Null when the caller had none: the operator's
    /// cancel arrives without one and its notice goes without a manage link, exactly as it does
    /// today.
    /// </summary>
    public string? Secret { get; set; }

    /// <summary>
    /// Where the meeting was before it moved. <see cref="FollowUpType.Rescheduled"/> is the only
    /// type that sets it — nothing in the schema enforces that — and it is carried rather than
    /// derived because the booking row is overwritten in place; past the move, nothing else
    /// remembers the hour the invitee was told to expect.
    /// </summary>
    public long? PreviousStartUtc { get; set; }

    public long CreatedAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}
