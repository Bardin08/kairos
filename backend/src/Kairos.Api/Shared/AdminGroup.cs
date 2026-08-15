namespace Kairos.Api.Shared;

/// <summary>
/// The authorization policy and the route prefix that stand between an anonymous caller and the
/// operator's workbench.
/// </summary>
public static class AdminGroup
{
    public const string Policy = "admin";

    /// <summary>
    /// Every admin API route hangs off this and nothing else does. One place to read when asking
    /// "what is public?", which is the question that gets answered wrongly when the answer is
    /// scattered across twenty MapGet calls.
    /// </summary>
    /// <remarks>
    /// Three routes are outside it, all three by necessity rather than by oversight, and they are
    /// named here so this file stays the whole answer. <c>GET /admin/login</c> and
    /// <c>GET /admin/logout</c> are the challenge and the sign-out: nothing behind the gate could
    /// start either, since a challenge is a browser navigation to another origin. And
    /// <c>GET /api/v1/integrations/{provider}/callback</c> is the OAuth return leg — its path is
    /// registered byte-for-byte with the provider, and the caller is a browser holding that
    /// provider's redirect rather than a session of ours, so it is guarded by <c>state</c>.
    /// Everything else that an operator can reach is under this prefix.
    /// </remarks>
    public static RouteGroupBuilder MapAdmin(this WebApplication app) =>
        app.MapGroup($"{ApiVersions.V1}/admin").RequireAuthorization(Policy);
}
