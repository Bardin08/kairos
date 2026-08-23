using System.Diagnostics;
using System.Net;

using Kairos.Api.Persistence;
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
    /// The one lock a reader cannot get past. An ordinary write does not block a read in WAL mode
    /// — measured, and the reason readiness stays green through normal contention — so an
    /// exclusive locking mode is what it takes to reach the path under test. The write is what
    /// makes the connection take the lock; declaring the mode alone takes nothing.
    /// </summary>
    private const string LockAgainstReaders =
        "PRAGMA locking_mode = EXCLUSIVE; BEGIN IMMEDIATE; CREATE TABLE IF NOT EXISTS Blocker(Id);";

    /// <summary>
    /// Twice the check's own ceiling. Measured at 1.06 seconds bounded and 30.06 unbounded, so
    /// there is room for a slow machine and none for the regression this exists to catch.
    /// </summary>
    private static readonly TimeSpan SlowestAcceptableProbe =
        DatabaseHealthCheck.ProbeTimeout * ProbeTimeoutHeadroom;

    private const int ProbeTimeoutHeadroom = 2;

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

        await ShouldReportAsync(response, HealthStatus.Healthy, HttpStatusCode.OK);
    }

    /// <summary>
    /// The half that would pass by accident if the check never ran: a migrated database is ready.
    /// The body is read for the reason given on the liveness test — a 200 alone is also what the
    /// single-page fallback answers with, on any tree where the front end has been built.
    /// </summary>
    [Fact]
    public async Task Readiness_OverAMigratedDatabase_IsOk()
    {
        using var app = new KairosApp();

        var response = await app.Client.GetAsync(
            HealthEndpoints.ReadinessPath, TestContext.Current.CancellationToken);

        await ShouldReportAsync(response, HealthStatus.Healthy, HttpStatusCode.OK);
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
    [InlineData(true, HealthStatus.Unhealthy, HttpStatusCode.ServiceUnavailable)]
    [InlineData(false, HealthStatus.Healthy, HttpStatusCode.OK)]
    public async Task Readiness_WithAFailingCheck_AnswersForItOnlyWhenItIsTaggedForReadiness(
        bool tagged,
        HealthStatus reported,
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

        await ShouldReportAsync(response, reported, expected);
    }

    /// <summary>
    /// A contended database keeps the process in service. The lock here is held the one way that
    /// blocks a reader as well as a writer — WAL lets readers past an ordinary write, which is why
    /// the common case never reaches this path at all — and what is pinned is that the answer is
    /// 200 <c>Degraded</c> rather than the 503 that would take the only replica out of rotation
    /// over a database still able to serve a moment later.
    /// </summary>
    /// <remarks>
    /// The elapsed bound is the second half of it, and the half that fails loudly if the query
    /// timeout is dropped: unbounded, this answers after the provider's default 30 seconds, having
    /// held a pooled connection for all of them while the probe that asked gave up at one.
    /// </remarks>
    [Fact]
    public async Task Readiness_WhileTheDatabaseIsLockedAgainstReaders_IsDegradedRatherThanUnready()
    {
        using var app = new KairosApp();

        // Warmed so the pool holds an open connection: what is under test is a query that cannot
        // get the lock, not a connection that cannot be opened.
        var warm = await app.Client.GetAsync(
            HealthEndpoints.ReadinessPath, TestContext.Current.CancellationToken);
        warm.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var blocker = new SqliteConnection(app.ConnectionString);
        blocker.Open();
        await using (var command = blocker.CreateCommand())
        {
            command.CommandText = LockAgainstReaders;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var started = Stopwatch.GetTimestamp();
        var response = await app.Client.GetAsync(
            HealthEndpoints.ReadinessPath, TestContext.Current.CancellationToken);
        var elapsed = Stopwatch.GetElapsedTime(started);

        await ShouldReportAsync(response, HealthStatus.Degraded, HttpStatusCode.OK);
        elapsed.ShouldBeLessThan(SlowestAcceptableProbe);
    }

    /// <summary>
    /// Both halves of the verb restriction, because <c>MapHealthChecks</c> maps every verb unless
    /// it is told not to — which left an anonymous <c>POST /readyz</c> running a database query for
    /// anybody who sent one. HEAD is asserted beside it so that narrowing this to GET alone has to
    /// be a decision rather than a side effect.
    /// </summary>
    [Theory]
    [InlineData(HealthEndpoints.LivenessPath)]
    [InlineData(HealthEndpoints.ReadinessPath)]
    public async Task AProbePath_AnswersAProbeVerb_AndRefusesAWriteVerb(string path)
    {
        using var app = new KairosApp();

        using var head = new HttpRequestMessage(HttpMethod.Head, path);
        var headed = await app.Client.SendAsync(head, TestContext.Current.CancellationToken);
        var posted = await app.Client.PostAsync(
            path, content: null, TestContext.Current.CancellationToken);

        headed.StatusCode.ShouldBe(HttpStatusCode.OK);
        posted.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
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

    /// <summary>
    /// The status and what the endpoint said, together. The body is half the assertion because the
    /// status alone does not distinguish a health endpoint from the single page: on a tree where
    /// the front end has been built, an unmapped probe path is answered 200 by
    /// <c>MapFallbackToFile</c>, and every OK here would go on passing over a route that no longer
    /// exists.
    /// </summary>
    private static async Task ShouldReportAsync(
        HttpResponseMessage response,
        HealthStatus reported,
        HttpStatusCode expected)
    {
        response.StatusCode.ShouldBe(expected);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldBe(reported.ToString());
    }
}
