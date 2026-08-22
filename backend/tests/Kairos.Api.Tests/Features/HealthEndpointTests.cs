using System.Net;

using Kairos.Api.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// Whether the process is alive and whether it can serve, which are two questions and not one.
/// The test that matters is the third: with the database gone the answers have to differ, because
/// a liveness probe that fails alongside readiness restarts a pod over a fault no restart repairs.
/// </summary>
public class HealthEndpointTests
{
    /// <summary>
    /// SQLite's two sidecars, deleted with the database rather than instead of it. Clearing the
    /// pool below closes the last connection, which checkpoints the write-ahead log and removes
    /// both — measured here, and true only while nothing else holds a connection open. This
    /// application ticks two hosted services on timers of their own, so the deletes cover all
    /// three files and what the next request meets stops depending on that timing.
    /// </summary>
    private const string WriteAheadLogSuffix = "-wal";

    private const string SharedMemorySuffix = "-shm";

    /// <summary>A check registered by something other than readiness, which is what it stands in for.</summary>
    private const string SomebodyElsesCheck = "somebody-elses-check";

    /// <summary>
    /// Anonymous, because a kubelet holds no session and never will. Asserted on the shipped
    /// pipeline rather than on the mapping, so an authorization convention added to the whole
    /// application later fails here rather than in production at the first probe.
    /// </summary>
    /// <remarks>
    /// The body is read as well as the status, and that is not belt and braces. In a deployment
    /// that has a built single page, <c>MapFallbackToFile</c> claims every path no route claims —
    /// so an unmapped <c>/healthz</c> answers 200 with the whole application, and a probe checking
    /// the status alone reports a process that is answering nothing as alive.
    /// </remarks>
    [Fact]
    public async Task Liveness_WithoutASession_IsOk()
    {
        using var app = new KairosApp();

        var response = await app.Client.GetAsync(
            HealthEndpoints.LivenessPath, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldBe(nameof(HealthStatus.Healthy));
    }

    /// <summary>
    /// The half that would pass by accident if the check never ran: a migrated database is ready.
    /// </summary>
    [Fact]
    public async Task Readiness_OverAMigratedDatabase_IsOk()
    {
        using var app = new KairosApp();

        var response = await app.Client.GetAsync(
            HealthEndpoints.ReadinessPath, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The tag is what readiness selects on, and a registration without it is not part of the
    /// answer. Both rows use the same failing check, so what is pinned is the tag deciding rather
    /// than the check failing.
    /// </summary>
    /// <remarks>
    /// Worth its own test because with one check registered a predicate matching the tag and a
    /// predicate matching everything behave identically, so nothing else here can tell them apart
    /// — and the difference is the whole guard against a check added elsewhere in the application
    /// silently joining readiness. <see cref="Kairos.Api.Persistence.DatabaseHealthCheck"/> says
    /// why that matters: a provider check joining this answer would take the pod out of service
    /// over a revoked grant.
    /// </remarks>
    [Theory]
    [InlineData(true, HttpStatusCode.ServiceUnavailable)]
    [InlineData(false, HttpStatusCode.OK)]
    public async Task Readiness_WithAFailingCheck_AnswersForItOnlyWhenItIsTaggedForReadiness(
        bool tagged,
        HttpStatusCode expected)
    {
        string[] tags = tagged ? [HealthEndpoints.ReadinessTag] : [];
        using var app = new KairosApp();
        using var client = app
            .WithWebHostBuilder(builder => builder.ConfigureServices(services => services
                .AddHealthChecks()
                .AddCheck(SomebodyElsesCheck, () => HealthCheckResult.Unhealthy(), tags)))
            .CreateClient();

        var response = await client.GetAsync(
            HealthEndpoints.ReadinessPath, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(expected);
    }

    /// <summary>
    /// The pair, in one test because the value is in the difference between the two answers.
    /// </summary>
    /// <remarks>
    /// Taking the file away is not enough on its own, twice over. The pool holds an open handle
    /// and the inode outlives the unlink, so the connection reads the database it has always read
    /// — hence the pool is cleared first, scoped to this database as the rest of this suite scopes
    /// it. And SQLite CREATES the file it is pointed at, so a connection to the deleted database
    /// opens and a check that only asks whether it opens answers healthy over an empty file. What
    /// makes the 503 real is that the check queries a table.
    /// </remarks>
    [Fact]
    public async Task Readiness_WithTheDatabaseDeleted_IsUnavailable_WhileLivenessStillAnswers()
    {
        using var app = new KairosApp();

        // Warmed first, so the pool being cleared below is a pool that exists.
        var warm = await app.Client.GetAsync(
            HealthEndpoints.ReadinessPath, TestContext.Current.CancellationToken);
        warm.StatusCode.ShouldBe(HttpStatusCode.OK);

        SqliteConnection.ClearPool(new SqliteConnection(app.ConnectionString));
        string[] files =
        [
            app.DatabasePath,
            $"{app.DatabasePath}{WriteAheadLogSuffix}",
            $"{app.DatabasePath}{SharedMemorySuffix}",
        ];
        foreach (var file in files)
        {
            File.Delete(file);
        }

        var liveness = await app.Client.GetAsync(
            HealthEndpoints.LivenessPath, TestContext.Current.CancellationToken);
        var readiness = await app.Client.GetAsync(
            HealthEndpoints.ReadinessPath, TestContext.Current.CancellationToken);

        liveness.StatusCode.ShouldBe(HttpStatusCode.OK);
        readiness.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }
}
