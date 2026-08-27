namespace Kairos.Api.Features.Invites;

/// <summary>One invite as a caller sees it. Never a URL: there is none to give back.</summary>
public sealed record InviteView(
    Guid Id,
    string State,
    IReadOnlyDictionary<string, string> Prefill,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt);
