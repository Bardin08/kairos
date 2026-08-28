using Kairos.Api.Features.Bookings;
using Kairos.Api.Persistence;
using Kairos.Api.Security;
using Kairos.Api.Shared;

using Microsoft.EntityFrameworkCore;

using NodaTime;

namespace Kairos.Api.Features.Invites;

/// <summary>
/// What the booking page needs before it draws a form.
/// </summary>
/// <param name="State">
/// Lower-cased <see cref="Persistence.Entities.InviteState"/>. Anything but <c>open</c> means the
/// page draws the ordinary booking form and a notice rather than an error: a spent invite is still
/// a link somebody can book through.
/// </param>
/// <param name="ExpiresAt">
/// When it stops working, or null if it never does. Answered whatever the state, and it earns its
/// place at both ends of one: on an open invite it is the deadline the invitee has left to act on,
/// because a deadline they cannot see is a page that stops working without saying why, and on an
/// expired one it is the date the notice names.
/// </param>
public sealed record InviteReadResponse(
    string Slug,
    string State,
    DateTimeOffset? ExpiresAt,
    IReadOnlyDictionary<string, string> Prefill);

/// <summary>
/// The invitee's side of a personal invite: one read, answering where the invite stands and what
/// to seed the form with. Everything the operator does with one is
/// <see cref="Admin.InvitesAdminEndpoints"/>, behind the admin group.
/// </summary>
public static class InvitesEndpoints
{
    /// <summary>
    /// Anonymous, and mapped on v1 rather than on the admin chain, because the token IS the
    /// credential — the shape <c>GET /bookings/{token}</c> already has, and the point of the
    /// feature: whoever holds the URL gets the prefill. Budgeted the same way that route is, a
    /// lookup by hash that computes nothing being a read, and stated here rather than left to a
    /// global default nobody reads — the reasoning
    /// <see cref="BookingsEndpoints.MapBookings"/> gives for its own seven.
    /// </summary>
    public static RouteGroupBuilder MapInvites(this RouteGroupBuilder group)
    {
        group.MapGet("/invites/{token}", GetAsync).WithName("GetInvite")
            .RateLimited(RateLimitClass.AnonymousRead);
        return group;
    }

    /// <summary>
    /// A 404 only for a token no invite has. Consumed, revoked and expired all answer 200 with
    /// their state: the page still has a booking form to draw, and refusing would take a working
    /// link away over a prefill that is merely stale.
    /// </summary>
    /// <remarks>
    /// The hash comes from <see cref="BookingWriter.Hash"/> and not from a digest minted here:
    /// that method lower-cases before hashing, so any other spelling of the same computation
    /// produces a value the unique index has never seen and every lookup answers 404.
    /// </remarks>
    private static async Task<InviteReadResponse> GetAsync(
        string token,
        KairosDbContext context,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var hash = BookingWriter.Hash(token);

        // The slug comes off the invite's own navigation rather than from a second read, the way
        // the operator's regenerate resolves one.
        var invite = await context.PersonalInvites.AsNoTracking()
            .Include(candidate => candidate.Link)
            .FirstOrDefaultAsync(candidate => candidate.TokenHash == hash, cancellationToken)
            // Not "Invite '{token}'". LinksEndpoints keeps the caller's slug out of its own
            // message to stop an arbitrary string reaching a ProblemDetails title; the same rule
            // applies here with more force, the token being a credential rather than a public name.
            ?? throw DomainException.NotFound("Invite");

        var state = InviteWriter.StateAt(invite, clock.GetCurrentInstant());

        return new InviteReadResponse(
            invite.Link.Slug,
            InviteWriter.NameOf(state),
            invite.ExpiresAt is { } expires ? DateTimeOffset.FromUnixTimeSeconds(expires) : null,
            InvitePrefill.Read(invite.Prefill));
    }
}
