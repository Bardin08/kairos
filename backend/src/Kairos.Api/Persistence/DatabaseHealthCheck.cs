using System.Data.Common;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Kairos.Api.Persistence;

/// <summary>Whether this process can still read its own database, which is all readiness means here.</summary>
/// <remarks>
/// The local database only. No provider can fail a booking — an absent one contributes no busy
/// time, unsendable mail is logged, the outbox keeps queueing — so naming one here would pull the
/// pod from the load balancer over a revoked grant elsewhere. Provider trouble surfaces on the
/// integration screen.
/// <para>
/// A table is queried rather than <c>CanConnectAsync</c>: on SQLite the provider CREATES the file
/// it is pointed at, so connecting to a database deleted out from under the process opens happily
/// and reports a healthy, empty one. Only a query through the schema tells the two apart. Pending
/// migrations are deliberately not part of the answer — <c>Program.cs</c> migrates before
/// <c>app.Run()</c>, so anything left to apply means this endpoint is never served.
/// </para>
/// <para>
/// A busy database is degraded, not unready — the same call
/// <see cref="Shared.DatabaseBusyExceptionHandler"/> made the other way. SQLITE_BUSY is a queue: the
/// read that timed out succeeds a moment later, and unready would pull the one replica out of
/// rotation over contention while it still answers reads.
/// </para>
/// </remarks>
public sealed class DatabaseHealthCheck(KairosDbContext context) : IHealthCheck
{
    /// <summary>
    /// How long the probe query may wait for a lock. The provider's default is 30 seconds — a
    /// pooled connection held that long per probe, three in flight at a ten-second period, against
    /// a kubelet that gave up after one. A probe that cannot answer quickly has answered.
    /// </summary>
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(QueryTimeoutSeconds);

    /// <summary>The same bound as <see cref="QueryTimeout"/>, in the unit the connection takes.</summary>
    private const int QueryTimeoutSeconds = 1;

    /// <summary>
    /// The registration's ceiling, a multiple so the commands below fire first: a registration
    /// timeout is reported as unready, undoing the decision above. Three, for a probe's two bounded
    /// commands — the pragmas on opening a connection, and the query.
    /// </summary>
    /// <remarks>
    /// A backstop, not the bound: a blocked SQLite call retries inside the native library, on the
    /// thread, out of reach of any cancellation token. Measured — with the commands unbounded this
    /// never fired and the probe answered after 30 seconds.
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
        // On the probe's own scope only, so a request writing a booking still gets the provider's
        // default 30 seconds. The connection's default covers the command the pragma interceptor
        // runs when EF opens it, which is where a probe against an exclusively locked database
        // actually stalls — measured at the full 30 seconds, before the query below was reached.
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
