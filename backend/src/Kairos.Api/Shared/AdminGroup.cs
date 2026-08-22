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
    /// Six routes are outside it, none of them by oversight, and they are named here so this file
    /// stays the whole answer. <c>GET /admin/login</c> and <c>GET /admin/logout</c> are the
    /// challenge and the sign-out: nothing behind the gate could start either, since a challenge
    /// is a browser navigation to another origin.
    /// <c>GET /api/v1/integrations/{provider}/callback</c> is the OAuth return leg — its path is
    /// registered byte-for-byte with the provider, and the caller is a browser holding that
    /// provider's redirect rather than a session of ours, so it is guarded by <c>state</c>. And
    /// <c>GET /api/v1/sign-in</c> answers where to sign in, for a browser that has just been
    /// refused: behind the gate it would 401 at exactly the reader it exists for, and it carries
    /// one string that is safe to hand anybody. See <see cref="Features.Admin.SignInEndpoints"/>.
    /// <c>GET /healthz</c> and <c>GET /readyz</c> are the liveness and readiness probes: anonymous
    /// because a kubelet holds no session, and outside the version prefix as well as outside this
    /// one because their paths are written into a deployment manifest that nobody re-reads, so a
    /// version bump must not move them. See <see cref="HealthEndpoints"/>.
    /// Everything else that an operator can reach is under this prefix.
    /// <para>
    /// Nothing here carries a rate limit, and that is a decision rather than an omission. Every
    /// route behind this policy is reachable only with a session that a member of the operator's
    /// group can hold, and the workbench works in bursts — a pattern editor saves a week a row at
    /// a time. A limit here would throttle the operator to defend against the operator. The
    /// anonymous routes are the ones with budgets, declared at their mapping sites; see
    /// <see cref="Security.RateLimitPolicies"/>.
    /// </para>
    /// <para>
    /// The six routes named above carry no limit either, and each has its own reason: the
    /// challenge and the sign-out only issue a redirect, <c>GET /api/v1/sign-in</c> answers one
    /// configured string and touches no database, and the OAuth callback verifies its signed
    /// <c>state</c> before it will speak to a provider — so a caller without one buys an HMAC check
    /// and a log line, never an outbound request. The two probes are polled by the orchestrator on
    /// a fixed interval, and refusing one is how a working pod gets restarted or taken out of
    /// service; liveness runs no check at all, and readiness costs one existence query against a
    /// single-row table. This paragraph is the record of that, because six anonymous routes with
    /// nothing said about them read as an oversight.
    /// </para>
    /// </remarks>
    public static RouteGroupBuilder MapAdmin(this WebApplication app) =>
        app.MapGroup($"{ApiVersions.V1}/admin").RequireAuthorization(Policy);
}
