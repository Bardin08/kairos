namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// A booking URL aimed at one person, good once. It is a token ON a link rather than a link of
/// its own: same pattern, same durations, same slots, and a form that arrives partly filled.
/// </summary>
/// <remarks>
/// The token hash is this row's identity and nothing else on it is. A name and an email are two
/// keys in <see cref="Prefill"/> rather than columns, because both are optional, either can be
/// empty, and neither is what the invite is looked up by — and because the backlog's custom
/// booking questions would otherwise cost a migration each.
/// </remarks>
public sealed class PersonalInvite
{
    /// <summary>UUIDv7, like every other key here.</summary>
    public Guid Id { get; set; }

    public Guid LinkId { get; set; }

    public Link Link { get; set; } = null!;

    /// <summary>
    /// SHA-256 of the token in the URL. The plaintext is never stored, exactly as
    /// <see cref="Booking.ManageTokenHash"/> and <see cref="BookingProposal.TokenHash"/> are not.
    /// A lost URL is answered by regenerating, not by reading one back.
    /// </summary>
    public required string TokenHash { get; set; }

    /// <summary>
    /// A JSON object, keyed by the booking payload's own field names, so a prefill is a partial
    /// <c>CreateBookingRequest</c>. <c>"{}"</c> when the operator set nothing — which is a valid
    /// invite that still consumes exactly once.
    /// </summary>
    public required string Prefill { get; set; }

    /// <summary>
    /// Unix seconds, as every timestamp on this row is, or null for no deadline. Compared against
    /// the clock by every reader, the way <see cref="BookingProposal.ExpiresAt"/> is: an invite
    /// past its deadline stops being open at that instant, whether or not anything has run to
    /// notice.
    /// <para>
    /// Two readers exist and they have to agree on the same second, so a third one belongs beside
    /// them rather than beside a fresh comparison. <c>InviteWriter.StateAt</c> folds this into the
    /// state every surface renders, and the conditional UPDATE in <c>BookingWriter.TryCreate</c>
    /// spends the invite — the same comparison a second time in SQL, because the database holding
    /// the lock cannot call the fold.
    /// </para>
    /// </summary>
    public long? ExpiresAt { get; set; }

    public long? ConsumedAt { get; set; }

    public long? RevokedAt { get; set; }

    /// <summary>
    /// Which booking spent it, and carrying no foreign key on purpose. SQLite checks one at the
    /// statement rather than at COMMIT unless it is declared DEFERRABLE, and EF emits no DEFERRABLE
    /// anywhere in this schema; the consuming UPDATE runs inside the booking's transaction and
    /// before the booking row exists, so a constraint here would buy nothing but a worse order.
    /// </summary>
    public Guid? BookingId { get; set; }

    public long CreatedAt { get; set; }
}
