using Kairos.Api.Features.Invites;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Shared;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

using NodaTime;

namespace Kairos.Api.Features.Admin;

/// <summary>
/// The whole body of the 201, and the only response in this API that carries an invite's URL.
/// Every later read answers <see cref="InviteView"/>, which has no field for one: only the SHA-256
/// of the token is stored, so a lost URL is answered by regenerating rather than by asking again.
/// </summary>
public sealed record InviteCreatedResponse(Guid Id, string Url);

/// <summary>
/// What the operator fills in on the invitee's behalf, and how long the URL is good for. Both
/// optional: an empty bag with no deadline is a valid invite — a single-use URL and nothing seeded.
/// </summary>
/// <remarks>
/// The bag is checked by <see cref="InvitePrefill.Validate"/> rather than by a DataAnnotation,
/// because what it may carry is a list of field names each with the bound the booking payload
/// gives that field, which is not a shape an attribute can state.
/// </remarks>
public sealed record CreateInviteRequest
{
    /// <summary>
    /// Nullable, and left without an initializer on purpose. System.Text.Json ignores the
    /// nullable-reference annotation on a property exactly as it ignores the one on a dictionary's
    /// values, so <c>{"prefill": null}</c> writes over any default an initializer sets and the
    /// validator then walks a null bag. Absent and explicitly null mean the same thing here — an
    /// invite with nothing seeded is valid — so one <c>?? []</c> at the single reader answers both,
    /// where an initializer answers only one of them and hides the other as a 500.
    /// </summary>
    public Dictionary<string, string>? Prefill { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>
/// The operator's side of personal invites: issue one, list them, take one back, replace one.
/// Every route hangs off <see cref="AdminGroup.MapAdmin"/> and adds no authorization of its own —
/// a per-route repeat would read as though the group's guarantee were optional — so an anonymous
/// caller is answered 401 or 403 rather than redirected. A fetch cannot follow a cross-origin
/// redirect.
/// </summary>
public static class InvitesAdminEndpoints
{
    /// <summary>
    /// An unknown key, a null value, or a value past the length the booking payload accepts. One
    /// code for all three because the caller's recovery is the same in every case — fix the bag
    /// and send it again — and the message names which key and what was wrong with it. The
    /// reasoning <see cref="LinksAdminEndpoints.DurationsInvalidCode"/> carries for its own three.
    /// </summary>
    public const string PrefillInvalidCode = "prefill_invalid";

    /// <summary>
    /// An invite born expired. A bug rather than a race: afterwards the row is indistinguishable
    /// from one that lapsed honestly, so issuance is the only place that can tell.
    /// </summary>
    public const string DeadlinePastCode = "expiry_in_the_past";

    /// <summary>
    /// Regenerate answers "the URL was lost", and only an open invite has one to lose. Consumed,
    /// somebody has already had their one booking and a replacement would be a second. Expired,
    /// the deadline is copied verbatim and the replacement would be born expired — the exact state
    /// <see cref="DeadlinePastCode"/> refuses. Revoked, the operator took it back on purpose, and
    /// reissuing it is what pressing Create does with none of the ambiguity about whether the old
    /// URL is meant to work again. So: one rule, open only.
    /// </summary>
    public const string InviteNotOpenCode = "invite_not_open";

    /// <summary>The route the frontend claims: <c>/book/:slug</c> in <c>App.tsx</c>.</summary>
    private const string BookingPath = "book";

    /// <summary>The query key the booking page reads the token off.</summary>
    private const string TokenQueryKey = "i";

    /// <summary>
    /// What a 404 from these routes names. Both are reachable, by different paths: list and create
    /// resolve the link first, so an unknown link id is answered as a missing link, while revoke
    /// and regenerate go straight to the invite query — an unknown link id there is simply an
    /// invite that is not under it, and is answered as a missing invite.
    /// </summary>
    private const string InviteName = "Invite";

    private const string LinkName = "Link";

    public static RouteGroupBuilder MapInvitesAdmin(this RouteGroupBuilder group)
    {
        group.MapGet("/invites", ListAllAsync).WithName("ListInvites");
        group.MapGet("/links/{id:guid}/invites", ListAsync).WithName("ListLinkInvites");
        group.MapPost("/links/{id:guid}/invites", CreateAsync).WithName("CreateInvite");
        group.MapPost("/links/{id:guid}/invites/{inviteId:guid}/revoke", RevokeAsync)
            .WithName("RevokeInvite");
        group.MapPost("/links/{id:guid}/invites/{inviteId:guid}/regenerate", RegenerateAsync)
            .WithName("RegenerateInvite");
        return group;
    }

    /// <summary>
    /// Every invite the operator holds, on every link, newest first and never a URL. No link is
    /// resolved first because none is named, so an empty list here means there are no invites at
    /// all. <see cref="ListAsync"/> cannot say that much: an empty list there is a link that
    /// exists and holds nothing, which is why a link that is not there is answered 404 instead —
    /// an operator on a stale page is told to reload.
    /// </summary>
    private static Task<InviteView[]> ListAllAsync(
        InviteReader reader,
        CancellationToken cancellationToken) =>
        reader.AllAsync(cancellationToken);

    /// <summary>
    /// One link's invites, newest first, and never a URL. 404 rather than an empty list on a link
    /// that is not there: an operator on a stale page needs to be told to reload rather than shown
    /// a link with nothing on it.
    /// </summary>
    private static async Task<InviteView[]> ListAsync(
        Guid id,
        KairosDbContext context,
        InviteReader reader,
        CancellationToken cancellationToken)
    {
        await RequireLinkAsync(context, id, cancellationToken);

        // Temporary, and deleted with this route: the reader now answers across every link, and
        // this route is retired in Task 7 of the invites-placement plan, in favour of the
        // cross-link GET /admin/invites.
        return [.. (await reader.AllAsync(cancellationToken)).Where(invite => invite.LinkId == id)];
    }

    private static async Task<Created<InviteCreatedResponse>> CreateAsync(
        Guid id,
        CreateInviteRequest request,
        HttpContext http,
        KairosDbContext context,
        InviteWriter writer,
        CancellationToken cancellationToken)
    {
        var slug = await RequireLinkAsync(context, id, cancellationToken);

        var result = writer.Issue(
            id,
            request.Prefill ?? [],
            request.ExpiresAt is { } deadline ? Instant.FromDateTimeOffset(deadline) : null);

        if (result.Refusal is not null)
        {
            throw Refused(result);
        }

        await context.SaveChangesAsync(cancellationToken);

        return Minted(http, slug, result.Invite!.Id, result.Token!);
    }

    /// <summary>
    /// Idempotent: the second press is the operator pressing twice, and <see cref="InviteWriter.Revoke"/>
    /// writes nothing over an invite somebody already spent.
    /// </summary>
    private static async Task<NoContent> RevokeAsync(
        Guid id,
        Guid inviteId,
        KairosDbContext context,
        InviteWriter writer,
        CancellationToken cancellationToken)
    {
        var invite = await context.PersonalInvites
            .FirstOrDefaultAsync(
                candidate => candidate.Id == inviteId && candidate.LinkId == id, cancellationToken)
            ?? throw DomainException.NotFound(InviteName);

        writer.Revoke(invite);
        await context.SaveChangesAsync(cancellationToken);

        return TypedResults.NoContent();
    }

    /// <summary>
    /// The state guard lives here rather than in <see cref="InviteWriter.Regenerate"/> because
    /// this is where the refusal channel is: that method returns a bare tuple with nowhere to put
    /// one, and without the guard it revokes nothing on a spent row and then adds a replacement
    /// regardless. See <see cref="InviteNotOpenCode"/> for why open is the only state it accepts.
    /// </summary>
    private static async Task<Created<InviteCreatedResponse>> RegenerateAsync(
        Guid id,
        Guid inviteId,
        HttpContext http,
        KairosDbContext context,
        InviteWriter writer,
        IClock clock,
        CancellationToken cancellationToken)
    {
        // The link comes off the invite's own navigation rather than from a second read: the URL
        // needs the slug, and the join answers that and the scoping in one query.
        var invite = await context.PersonalInvites
            .Include(candidate => candidate.Link)
            .FirstOrDefaultAsync(
                candidate => candidate.Id == inviteId && candidate.LinkId == id, cancellationToken)
            ?? throw DomainException.NotFound(InviteName);

        var state = InviteWriter.StateAt(invite, clock.GetCurrentInstant());
        if (state is not InviteState.Open)
        {
            throw DomainException.Conflict(
                InviteNotOpenCode,
                $"That invite is {InviteWriter.NameOf(state)}. Only an invite that is "
                + "still open can be replaced; issue a new one instead.");
        }

        var (replacement, token) = writer.Regenerate(invite);
        await context.SaveChangesAsync(cancellationToken);

        return Minted(http, invite.Link.Slug, replacement.Id, token);
    }

    /// <summary>
    /// The one place an invite URL is built, and it has to be a place holding the request: the
    /// origin is the one the operator is actually running Kairos on. Assembling it in the browser
    /// instead would bake the public origin into a bundle built once and deployed everywhere.
    /// </summary>
    /// <remarks>
    /// No Location header. A 201 names the resource it created, and no route answers with one
    /// invite: the URL is in this response and nowhere else, and the list has no field for it.
    ///
    /// Neither part is escaped, and neither needs to be — <see cref="SaveLinkRequest.Slug"/> is
    /// refused at the write unless it is lowercase alphanumerics and hyphens, and the token is hex.
    /// </remarks>
    private static Created<InviteCreatedResponse> Minted(
        HttpContext http, string slug, Guid inviteId, string token) =>
        TypedResults.Created(
            (string?)null,
            new InviteCreatedResponse(
                inviteId,
                $"{http.Request.Scheme}://{http.Request.Host}/{BookingPath}/{slug}"
                + $"?{TokenQueryKey}={token}"));

    /// <summary>
    /// A refusal from the writer as the failure it means. Both are the operator's own input, so
    /// both are Invalid rather than Conflict: nothing raced them.
    /// </summary>
    private static DomainException Refused(IssueResult result) => result switch
    {
        // The verdict is matched out rather than dereferenced: the factories on IssueResult are
        // what make a prefill refusal arrive with one, and this reads it the same way round.
        { Refusal: IssueRefusal.PrefillInvalid, Prefill: { } prefill } => DomainException.Invalid(
            PrefillInvalidCode, InvitePrefill.Describe(prefill)),
        { Refusal: IssueRefusal.DeadlinePast } => DomainException.Invalid(
            DeadlinePastCode, "That deadline has already passed."),
        _ => throw new ArgumentOutOfRangeException(
            nameof(result), result, "Unknown issue refusal."),
    };

    /// <summary>
    /// The link, or a 404. It answers with the slug, which is what a URL needs, and the caller
    /// that wants only the existence check discards it — the shape
    /// <see cref="Bookings.BookingMover.RequireOfferedAsync"/> already has, and the prefix
    /// <see cref="LinksAdminEndpoints"/> gives its own two resolve-or-throw helpers.
    /// </summary>
    private static async Task<string> RequireLinkAsync(
        KairosDbContext context,
        Guid id,
        CancellationToken cancellationToken) =>
        await context.Links.AsNoTracking()
            .Where(link => link.Id == id)
            .Select(link => link.Slug)
            .FirstOrDefaultAsync(cancellationToken)
        ?? throw DomainException.NotFound(LinkName);
}
