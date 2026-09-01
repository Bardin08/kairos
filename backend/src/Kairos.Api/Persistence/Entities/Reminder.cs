namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// One mail, scheduled. Written in the same transaction as the booking it belongs to, so
/// "the booking exists and nothing will remind anybody" is impossible rather than unlikely.
/// </summary>
/// <remarks>
/// A row exists only if its <see cref="DueAt"/> was in the future when it was written. That is
/// the whole of this feature's edge-case handling: a booking made three hours before it starts,
/// or moved from next week to this evening, gets no row rather than a row the sweeper has to
/// reason about. So a <see cref="DueAt"/> in the past can only mean the process was down over it,
/// and the sweeper sends late instead of deciding whether to.
/// </remarks>
public sealed class Reminder
{
    /// <summary>UUIDv7, like every other key here.</summary>
    public Guid Id { get; set; }

    public Guid BookingId { get; set; }

    public Booking Booking { get; set; } = null!;

    /// <summary>Unix seconds. The meeting's start less the fixed lead.</summary>
    public long DueAt { get; set; }

    /// <summary>
    /// The manage token, encrypted by <see cref="Security.SecretProtector"/>. The notice this row
    /// schedules offers "Manage or cancel" beside the join link, and it is composed a day later by
    /// a sweeper that was not there when the token was minted — so the row has to carry the token
    /// itself, which is why this is ciphertext rather than the SHA-256
    /// <see cref="Booking.ManageTokenHash"/> keeps. Null two ways, and such a reminder goes out
    /// without the secondary link rather than not going: a booking written before this column
    /// existed, and a booking made INSIDE the lead and later moved outside it — that one never had
    /// a row, so <see cref="Features.Bookings.BookingMover"/> has no ciphertext to carry across and
    /// cannot mint one, for the reason its carry-forward states.
    /// </summary>
    public string? Secret { get; set; }

    /// <summary>
    /// Null until the relay accepted it. Stamped on the answer rather than on the attempt, so a
    /// tick that dies mid-send and a relay that refuses leave the same row and get the same
    /// answer: send again. A duplicate reminder is an annoyance; a missing one is the feature.
    /// </summary>
    public long? SentAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}
