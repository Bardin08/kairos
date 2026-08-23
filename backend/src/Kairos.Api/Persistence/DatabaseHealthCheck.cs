using System.Data.Common;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Kairos.Api.Persistence;

/// <summary>
/// Whether this process can still read its own database, which is the whole of what readiness
/// means here.
/// </summary>
/// <remarks>
/// The local database and nothing else — not the calendar provider, not the mail relay, not the
/// webhook receiver, not whatever adapter ships next. None of those can fail a booking: an
/// unconfigured or disconnected provider contributes no busy time, mail that cannot be sent is
/// logged, and the outbox keeps queueing for a receiver that refuses. A readiness check naming one
/// would take the pod out of the load balancer over a revoked grant somewhere else, turning a
/// broken integration into an outage for every invitee who only wanted a slot. Provider trouble
/// surfaces on the integration screen, where the operator can act on it.
/// <para>
/// A table is queried rather than <c>CanConnectAsync</c>, because on SQLite connecting proves
/// nothing: the provider CREATES the file it is pointed at, so a connection to a database deleted
/// or moved out from under the process opens happily and reports a healthy, empty one. That is the
/// state this check exists to catch, and a query through the schema is what tells the two apart.
/// </para>
/// <para>
/// Pending migrations are deliberately not part of the answer. <c>Program.cs</c> runs
/// <c>Database.Migrate()</c> before <c>app.Run()</c>, so a process with anything left to apply
/// never reaches the point where this endpoint is served; checking would spend a round-trip per
/// probe on a question already true by construction, at whatever interval the orchestrator polls.
/// </para>
/// <para>
/// A busy database is degraded and not unready, the same decision
/// <see cref="Shared.DatabaseBusyExceptionHandler"/> made in the other direction: SQLITE_BUSY is a
/// queue rather than a fault, and the read that timed out succeeds a moment later. Reported as
/// unready it would take the one replica out of rotation over contention, and every invitee would
/// meet the ingress's 503 for a database still answering reads. Degraded keeps the process in
/// service and still says out loud that something is contended.
/// </para>
/// </remarks>
public sealed class DatabaseHealthCheck(KairosDbContext context) : IHealthCheck
{
    /// <summary>
    /// How long the probe query may wait for a lock before it gives up and says so. The provider's
    /// default is 30 seconds — a pooled connection held that long per probe while a kubelet that
    /// gave up after one starts the next, three in flight at a ten-second period, competing with
    /// real traffic for the same pool. A probe that cannot answer quickly has answered.
    /// </summary>
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(QueryTimeoutSeconds);

    /// <summary>The same bound as <see cref="QueryTimeout"/>, in the unit the connection takes.</summary>
    private const int QueryTimeoutSeconds = 1;

    /// <summary>
    /// The registration's ceiling, a multiple rather than a number of its own because the commands
    /// below have to fire first. The health check service reports a registration timeout as the
    /// failure status — unready — so a ceiling that beat them would classify a contended database
    /// as unready and undo the decision recorded above. Three, because a probe issues two bounded
    /// commands: the pragmas on opening a connection, and the query.
    /// </summary>
    /// <remarks>
    /// It is a backstop and not the bound. A blocked SQLite call retries inside the native library
    /// until its own command timeout expires, on the thread, where no cancellation token reaches it
    /// — measured: with the commands unbounded this ceiling did not fire at all and the probe
    /// answered after 30 seconds. What bounds this check is the two timeouts below.
    /// </remarks>
    public static readonly TimeSpan ProbeTimeout = QueryTimeout * TimeoutHeadroom;

    private const int TimeoutHeadroom = 3;

    private const string Unreadable =
        "The Kairos database did not answer a read. It may be missing, replaced or unreadable at "
        + "the path this process is configured with.";

    private const string Contended =
        "The Kairos database did not hand over a read lock in time. It is contended rather than "
        + "broken, so this process stays in service.";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext check,
        CancellationToken cancellationToken = default)
    {
        // Both on the probe's own scope and nowhere else: the health check service resolves this
        // check in a scope of its own, so a request writing a booking still gets the provider's
        // default 30 seconds. A booking is worth waiting for and a probe is not.
        //
        // The connection's default covers the command the pragma interceptor runs when EF opens it,
        // which is where a probe against an exclusively locked database actually stalls — measured
        // at the full 30 seconds, before the query below was reached. That command is not this
        // code's to configure directly, and it inherits this.
        ((SqliteConnection)context.Database.GetDbConnection()).DefaultTimeout = QueryTimeoutSeconds;
        context.Database.SetCommandTimeout(QueryTimeout);

        try
        {
            await context.Settings.AnyAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (DbException failure)
        {
            return SqliteErrors.IsDatabaseBusy(failure)
                ? HealthCheckResult.Degraded(Contended, failure)
                : HealthCheckResult.Unhealthy(Unreadable, failure);
        }
    }
}
