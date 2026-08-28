using System.Security.Cryptography;

using Kairos.Api.Features.Bookings;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using NodaTime;

namespace Kairos.Api.Features.Invites;

/// <summary>
/// Generating, revoking and regenerating an invite, and the one fold that says where one stands.
/// </summary>
/// <remarks>
/// It adds and mutates, and it never saves: the caller decides what commits with it.
///
/// Consuming is NOT here. It happens inside <see cref="BookingWriter"/>'s own BEGIN IMMEDIATE,
/// because a single-use token spent outside the transaction that checks the slot is a token two
/// callers can spend.
/// </remarks>
public sealed class InviteWriter(KairosDbContext context, IClock clock)
{
    /// <summary>Matching the manage token and the proposal token, for no reason other than that they match.</summary>
    private const int TokenBytes = 32;

    /// <summary>
    /// Where an invite stands, folded from what is stored plus the clock. The only place
    /// <see cref="PersonalInvite.ExpiresAt"/> is compared against a clock, which is what keeps a
    /// lapsed invite lapsed everywhere at once without a background job marking it.
    /// </summary>
    /// <remarks>
    /// Precedence is consumed, then revoked, then expired. A row can satisfy more than one at
    /// once and the operator is told what actually happened to it: somebody booked through this
    /// invite, and neither a later revocation nor a lapsed deadline unhappens that. Reporting
    /// Revoked or Expired over a real consumption would say the invite went unused.
    ///
    /// The deadline second itself is still open, which is the opposite of what
    /// <see cref="ProposalWriter.StateAt"/> answers at a hold's expiry second, and deliberate. A
    /// hold's expiry is derived rather than chosen — <see cref="ProposalWriter.Add"/> computes it
    /// as the current instant plus the operator's hold length in hours — so no one named that
    /// second and neither side of it is anybody's. An invite's deadline arrives whole, as an
    /// instant rather than a length of time, so the second in it was picked, and an invite is good
    /// through the second it was given.
    /// </remarks>
    public static InviteState StateAt(PersonalInvite invite, Instant now)
    {
        if (invite.ConsumedAt is not null)
        {
            return InviteState.Consumed;
        }

        if (invite.RevokedAt is not null)
        {
            return InviteState.Revoked;
        }

        return invite.ExpiresAt is { } expiresAt && expiresAt < now.ToUnixTimeSeconds()
            ? InviteState.Expired
            : InviteState.Open;
    }

    /// <summary>The plaintext leaves this method and is never stored; only the hash beside it ever reaches a row.</summary>
    public static (string Token, string Hash) NewToken()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenBytes)).ToLowerInvariant();
        return (token, BookingWriter.Hash(token));
    }

    /// <summary>
    /// Issuing an invite, validation included: the prefill and the deadline are decided here
    /// rather than at the endpoint, so a second caller cannot reach <see cref="Add"/> with a bag
    /// this one would have refused.
    /// </summary>
    /// <remarks>
    /// The endpoint keeps exactly two jobs it cannot delegate — reading the request and turning
    /// this result into a status code. The URL it then builds needs the request's own origin, so
    /// that assembly stays at the edge too; the token in it comes from here.
    ///
    /// The clock is read once and handed down, the way <see cref="ProposalWriter.Add"/> hoists one
    /// reading for both of its timestamps: two clock reads in one issuance are two answers, which
    /// is the reasoning <see cref="ProposalWriter.WithdrawOpenAsync"/> states.
    /// </remarks>
    public IssueResult Issue(
        Guid linkId,
        IReadOnlyDictionary<string, string> prefill,
        Instant? expiresAt)
    {
        var validation = InvitePrefill.Validate(prefill);
        if (!validation.IsValid)
        {
            return IssueResult.RefusedPrefill(validation);
        }

        var now = clock.GetCurrentInstant();

        // An invite born expired is a bug, and this is the only place that can tell: by the time
        // anything else looks at the row, "expired" is indistinguishable from one that lapsed
        // honestly.
        if (expiresAt is { } deadline && deadline < now)
        {
            return IssueResult.RefusedDeadline();
        }

        var (invite, token) = Add(linkId, prefill, expiresAt, now);
        return IssueResult.Issued(invite, token);
    }

    /// <summary>
    /// Marks it revoked, unless somebody already used it.
    /// </summary>
    /// <remarks>
    /// Idempotent on purpose: revoking twice is the operator clicking twice, and the second click
    /// is not an error worth a page. Refusing on a consumed invite is the other half of
    /// <see cref="StateAt"/>'s precedence — there is nothing left to revoke, and writing the
    /// timestamp anyway would only create the ambiguous row that fold then has to talk its way
    /// out of.
    /// </remarks>
    public void Revoke(PersonalInvite invite)
    {
        if (invite.ConsumedAt is not null)
        {
            return;
        }

        invite.RevokedAt ??= clock.GetCurrentInstant().ToUnixTimeSeconds();
    }

    /// <summary>
    /// The answer to a lost URL: revoke this one, issue a replacement carrying the same prefill
    /// and the same deadline. Per invite rather than per link, so it is not a cap on how many a
    /// link may have outstanding.
    /// </summary>
    public (PersonalInvite Invite, string Token) Regenerate(PersonalInvite invite)
    {
        var now = clock.GetCurrentInstant();

        Revoke(invite);

        var expiresAt = invite.ExpiresAt is { } seconds
            ? Instant.FromUnixTimeSeconds(seconds)
            : (Instant?)null;

        // Straight to Add rather than through Issue: this bag and this deadline were validated
        // when the invite they are copied from was issued, and re-refusing them now would mean a
        // rule tightened since then leaves the operator unable to reissue a link they already
        // sent — with no way to fix it, because the prefill is not editable after the fact.
        return Add(invite.LinkId, InvitePrefill.Read(invite.Prefill), expiresAt, now);
    }

    /// <param name="now">
    /// The caller's reading, not one of its own, so everything one issuance stamps is stamped with
    /// the same instant.
    /// </param>
    private (PersonalInvite Invite, string Token) Add(
        Guid linkId,
        IReadOnlyDictionary<string, string> prefill,
        Instant? expiresAt,
        Instant now)
    {
        var (token, hash) = NewToken();

        var invite = new PersonalInvite
        {
            Id = Guid.CreateVersion7(),
            LinkId = linkId,
            TokenHash = hash,
            Prefill = InvitePrefill.Serialize(prefill),
            ExpiresAt = expiresAt?.ToUnixTimeSeconds(),
            CreatedAt = now.ToUnixTimeSeconds(),
        };

        context.PersonalInvites.Add(invite);
        return (invite, token);
    }
}
