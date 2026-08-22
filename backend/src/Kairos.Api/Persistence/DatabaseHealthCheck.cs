using System.Data.Common;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Kairos.Api.Persistence;

/// <summary>
/// Whether this process can still read its own database, which is the whole of what readiness
/// means here.
/// </summary>
/// <remarks>
/// It checks the local database and nothing else — not the calendar provider, not the mail relay,
/// not the webhook receiver, and not whatever adapter ships next. None of those can fail a
/// booking: an unconfigured or disconnected provider contributes no busy time, mail that cannot be
/// sent is logged, and the outbox keeps queueing for a receiver that refuses. A readiness check
/// that named one would take the pod out of the load balancer over a revoked grant somewhere else
/// — turning a broken integration into an outage, and doing it to every invitee who only wanted a
/// slot. Provider trouble surfaces on the integration screen, where the operator can act on it.
/// <para>
/// A table is queried rather than <c>CanConnectAsync</c>, because on SQLite connecting proves
/// nothing: the provider CREATES the file it is pointed at, so a connection to a database that has
/// been deleted or moved out from under the process opens happily and reports a healthy, empty
/// database. That is precisely the state this check exists to catch, and putting a query through
/// the schema is what tells the two apart.
/// </para>
/// <para>
/// Pending migrations are deliberately not part of the answer. <c>Program.cs</c> runs
/// <c>Database.Migrate()</c> before <c>app.Run()</c>, so a process with anything left to apply
/// never reaches the point where this endpoint is served — the check would spend a second
/// round-trip per probe to answer a question that is already true by construction, and would go on
/// answering it at whatever interval the orchestrator polls.
/// </para>
/// </remarks>
public sealed class DatabaseHealthCheck(KairosDbContext context) : IHealthCheck
{
    private const string Unreadable =
        "The Kairos database did not answer a read. It may be missing, replaced or unreadable at "
        + "the path this process is configured with.";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext check,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await context.Settings.AnyAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (DbException failure)
        {
            return HealthCheckResult.Unhealthy(Unreadable, failure);
        }
    }
}
