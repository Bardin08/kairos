using Kairos.Api.Persistence;

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;

namespace Kairos.Api.Shared;

/// <summary>
/// The two questions an orchestrator asks — is this process alive, and can it serve — on two paths
/// that answer them differently. Both are anonymous, because a kubelet holds no session.
/// </summary>
/// <remarks>
/// Outside <see cref="ApiVersions.V1"/> deliberately. A probe path is written into a deployment
/// manifest that is edited by hand and rarely re-read, so a version bump must not be able to move
/// it out from under a running cluster.
/// </remarks>
public static class HealthEndpoints
{
    /// <summary>Is the process alive.</summary>
    public const string LivenessPath = "/healthz";

    /// <summary>Can the process serve.</summary>
    public const string ReadinessPath = "/readyz";

    /// <summary>What a check has to be tagged with to be part of the readiness answer.</summary>
    public const string ReadinessTag = "ready";

    /// <summary>The name the database check reports under.</summary>
    public const string DatabaseCheckName = "database";

    /// <summary>
    /// The verbs a probe uses, and the only ones these routes answer. <c>MapHealthChecks</c> maps
    /// every method by default, which would leave an anonymous, unmetered <c>POST /readyz</c>
    /// running a database query — a route nothing needs, on the one endpoint here that touches
    /// disk.
    /// </summary>
    private static readonly string[] ProbeMethods = [HttpMethods.Get, HttpMethods.Head];

    public static IServiceCollection AddHealthEndpoints(this IServiceCollection services) =>
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>(
                DatabaseCheckName,
                tags: [ReadinessTag],
                timeout: DatabaseHealthCheck.ProbeTimeout)
            .Services;

    public static void MapHealth(this IEndpointRouteBuilder app)
    {
        // The predicate is the whole of liveness: it selects no check, so this route runs nothing
        // and answers 200 because the process answered at all. That is the question a kubelet is
        // asking, and the only one a restart can fix. A liveness probe that consulted the database
        // would restart a healthy pod over a lock held for a second, turning a stall into an
        // outage — and it would do it hardest under exactly the load that caused the lock.
        app.MapHealthChecks(LivenessPath, new HealthCheckOptions { Predicate = _ => false })
            .WithMetadata(new HttpMethodMetadata(ProbeMethods));

        // Readiness runs what is tagged for it, which today is the database and by design nothing
        // remote; see DatabaseHealthCheck for why a provider must never be added here.
        app.MapHealthChecks(
                ReadinessPath,
                new HealthCheckOptions
                {
                    Predicate = registration => registration.Tags.Contains(ReadinessTag),
                })
            .WithMetadata(new HttpMethodMetadata(ProbeMethods));
    }
}
