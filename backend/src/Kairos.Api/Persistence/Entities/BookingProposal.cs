namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// A move the operator has offered and the invitee has not answered. It exists only while that is
/// true, which is why it is a row rather than columns on <see cref="Booking"/>.
/// </summary>
/// <remarks>
/// While <see cref="State"/> is <see cref="ProposalState.Open"/> and <see cref="ExpiresAt"/> is in
/// the future, this row's interval is busy for everyone — otherwise a stranger books the proposed
/// slot while the invitee is reading the mail, and the acceptance fails on a slot that no longer
/// exists. The booking's own slot stays confirmed throughout, so a meeting under proposal holds
/// two intervals and gives one of them back whichever way the invitee answers.
/// </remarks>
public sealed class BookingProposal
{
    /// <summary>UUIDv7, like every other key here.</summary>
    public Guid Id { get; set; }

    public Guid BookingId { get; set; }

    public Booking Booking { get; set; } = null!;

    /// <summary>Unix seconds, as <see cref="Booking.StartUtc"/> is, for the same margin arithmetic.</summary>
    public long StartUtc { get; set; }

    public long EndUtc { get; set; }

    /// <summary>
    /// When the hold lapses. Compared against the clock by every reader rather than trusted to a
    /// background job: a hold whose expiry has passed stops holding at that instant even if
    /// nothing has run to mark it <see cref="ProposalState.Expired"/> yet.
    /// </summary>
    public long ExpiresAt { get; set; }

    public long CreatedAt { get; set; }

    public ProposalState State { get; set; }

    /// <summary>
    /// SHA-256 of the token in the invitee's accept and decline links. The plaintext is never
    /// stored, exactly as <see cref="Booking.ManageTokenHash"/> is not — and this token exists at
    /// all because the manage one cannot be recovered for a mail the operator triggers.
    /// </summary>
    public required string TokenHash { get; set; }

    /// <summary>Null while Open. When the invitee answered, or when the sweeper gave up on them.</summary>
    public long? DecidedAt { get; set; }

    /// <summary>
    /// When the sweeper announced this row's lapse, and nothing wider than that. It is the
    /// sweeper's idempotency marker — a tick that crashes after sending must not mail the same
    /// lapse again on the next one — and the sweep's own predicate is its only reader.
    /// </summary>
    /// <remarks>
    /// It is not a record of the operator having been told, and a decline is the case that proves
    /// it: that path mails the operator and leaves this null. Stamping it there would be worse
    /// than the gap it closes, because the only write available is the conditional UPDATE that
    /// claims the row, which runs BEFORE the send — the column would then say a message went out
    /// on the strength of a decision that had only just been made.
    /// </remarks>
    public long? NotifiedAt { get; set; }
}
