namespace Kairos.Api.Shared;

public static class AdminGroup
{
    public const string Policy = "admin";

    /// <summary>
    /// Every admin API route hangs off this prefix and nothing else does, so one file answers "what
    /// is public?" instead of twenty scattered MapGet calls.
    /// </summary>
    /// <remarks>
    /// Six routes sit outside it, each deliberately:
    /// <list type="bullet">
    /// <item><c>GET /admin/login</c> and <c>GET /admin/logout</c> — the challenge and the sign-out.
    /// Nothing behind the gate could start either, since a challenge is a browser navigation to
    /// another origin.</item>
    /// <item><c>GET /api/v1/integrations/{provider}/callback</c> — the OAuth return leg. Its path is
    /// registered byte-for-byte with the provider, and the caller is a browser holding that
    /// provider's redirect rather than a session of ours, so <c>state</c> guards it.</item>
    /// <item><c>GET /api/v1/sign-in</c> — answers where to sign in, for a browser that has just been
    /// refused. Behind the gate it would 401 at exactly the reader it exists for, and it carries one
    /// string that is safe to hand anybody. See <see cref="Features.Admin.SignInEndpoints"/>.</item>
    /// <item><c>/healthz</c> and <c>/readyz</c> — the liveness and readiness probes, answering GET
    /// and HEAD and refusing every other verb. Anonymous because a kubelet holds no session, and
    /// outside the version prefix as well as this one because their paths are written into a
    /// deployment manifest that nobody re-reads, so a version bump must not move them. See
    /// <see cref="HealthEndpoints"/>.</item>
    /// </list>
    /// Everything else an operator can reach is under this prefix.
    /// <para>
    /// Nothing behind this policy carries a rate limit, and that is a decision. Every route here
    /// needs a session only a member of the operator's group can hold, and the workbench works in
    /// bursts — a pattern editor saves a week a row at a time — so a limit would throttle the
    /// operator to defend against the operator. The anonymous routes carry the budgets, declared at
    /// their mapping sites; see <see cref="Security.RateLimitPolicies"/>.
    /// </para>
    /// <para>
    /// The six routes above carry no limit either, each for its own reason: the challenge and the
    /// sign-out only issue a redirect; <c>GET /api/v1/sign-in</c> answers one configured string and
    /// touches no database; the OAuth callback verifies its signed <c>state</c> before it will speak
    /// to a provider, so a caller without one buys an HMAC check and a log line, never an outbound
    /// request; and the two probes are polled by the orchestrator on a fixed interval, where
    /// refusing one is how a working pod gets restarted or taken out of service — liveness runs no
    /// check at all, and readiness costs one existence query against a single-row table.
    /// </para>
    /// </remarks>
    public static RouteGroupBuilder MapAdmin(this WebApplication app) =>
        app.MapGroup($"{ApiVersions.V1}/admin").RequireAuthorization(Policy);
}
