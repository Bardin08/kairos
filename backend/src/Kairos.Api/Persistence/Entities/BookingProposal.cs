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
    /// When the sweeper announced this row's lapse, and nothing wider than that. It is written
    /// after the message has gone, in a save of its own, and the sweep's predicate is its only
    /// reader.
    /// </summary>
    /// <remarks>
    /// Written last on purpose, which makes the notice at-least-once: a tick that dies between the
    /// send and this leaves the row Expired with a null marker, and the next tick sends again
    /// rather than staying silent. A duplicate "nobody answered your suggestion" is an annoyance;
    /// a lost one is an operator who never finds out, over a meeting they still believe is being
    /// decided.
    ///
    /// It is not a record of the operator having been told, and a decline is the case that proves
    /// it: that path mails the operator and leaves this null. Nothing reads it there — the sweep
    /// excludes an answered row on its state, whatever this holds — and the only write available on
    /// that path is the conditional UPDATE that claims the row, which runs before its send and
    /// would record a message that had not left yet.
    /// </remarks>
    public long? NotifiedAt { get; set; }
}
