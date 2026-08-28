namespace Kairos.Api.Features.Invites;

/// <summary>One invite as a caller sees it. Never a URL: there is none to give back.</summary>
/// <param name="LinkId">
/// What the row's own actions are routed by — revoke and regenerate are addressed under the link,
/// not under the invite alone.
/// </param>
/// <param name="LinkName">
/// The link as the operator named it, because a list spanning links is read under a Link column
/// and a slug is not what they called it.
/// </param>
public sealed record InviteView(
    Guid Id,
    Guid LinkId,
    string LinkName,
    string State,
    IReadOnlyDictionary<string, string> Prefill,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt);
