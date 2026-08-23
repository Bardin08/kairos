namespace Kairos.Api.Shared;

public static class AdminGroup
{
    public const string Policy = "admin";

    /// <summary>
    /// Every admin API route hangs off this prefix and nothing else does, so one file answers "what
    /// is public?".
    /// </summary>
    /// <remarks>
    /// Six routes sit outside it, each deliberately, and each unlimited for its own reason:
    /// <list type="bullet">
    /// <item><c>GET /admin/login</c> — the challenge, a browser navigation to another origin that
    /// nothing behind the gate could start. Issues only a redirect.</item>
    /// <item><c>GET /admin/logout</c> — the sign-out, a redirect for the same reason.</item>
    /// <item><c>GET /api/v1/integrations/{provider}/callback</c> — the OAuth return leg, its path
    /// registered byte-for-byte with the provider and its caller holding that provider's redirect
    /// rather than our session. Signed <c>state</c> guards it: without one a caller buys an HMAC
    /// check and a log line, never an outbound request.</item>
    /// <item><c>GET /api/v1/sign-in</c> — where to sign in, for a browser just refused; behind the
    /// gate it would 401 at the one reader it exists for. One configured string, no database. See
    /// <see cref="Features.Admin.SignInEndpoints"/>.</item>
    /// <item><c>/healthz</c> — liveness, GET and HEAD only, running no check at all. Anonymous
    /// because a kubelet holds no session.</item>
    /// <item><c>/readyz</c> — readiness, one existence query on a single-row table. Both probes are
    /// polled on a fixed interval and refusing one restarts a working pod; both sit outside the
    /// version prefix too, their paths being in a manifest a version bump must not move. See
    /// <see cref="HealthEndpoints"/>.</item>
    /// </list>
    /// <para>
    /// Nothing behind the policy is limited either, by decision: it needs a session only the
    /// operator's group holds, and the workbench works in bursts — a pattern editor saves a week a
    /// row at a time — so a limit would throttle the operator against the operator. The budgets are
    /// on the anonymous routes, at their mapping sites; see <see cref="Security.RateLimitPolicies"/>.
    /// </para>
    /// </remarks>
    public static RouteGroupBuilder MapAdmin(this WebApplication app) =>
        app.MapGroup($"{ApiVersions.V1}/admin").RequireAuthorization(Policy);
}
