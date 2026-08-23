using Kairos.Api.Shared;

using NodaTime.Text;

namespace Kairos.Api.Features.Admin;

/// <summary>
/// One alert as the workbench renders it. Every field is source-neutral, so the shell lists
/// whatever the channel returns and holds copy only for the codes it knows.
/// </summary>
/// <param name="Since">ISO instant of the last time the thing worked, or null.</param>
/// <param name="Details">
/// A flat map, always present and often empty. The shell renders only the keys its copy names, so
/// a source adding one never breaks a screen that has not learned it.
/// </param>
public sealed record AlertResponse(
    string Code,
    string Severity,
    string? Subject,
    string? Since,
    string? Link,
    IReadOnlyDictionary<string, string> Details);

/// <summary>
/// <para>
/// On the admin group like every other admin route, so rule 19's access decision is stated where
/// the group is built and rule 20's rate-limit answer is the group's argued "none". The shell calls
/// this on mount and on every navigation, which makes "within one page load" true without a poll.
/// </para>
/// <para>
/// Ordered by severity alone, deliberately not by <see cref="OperatorAlert.Since"/>: a fault with
/// no known last success sorts first or last depending on how null is treated, and neither is a
/// claim this can make honestly.
/// </para>
/// </summary>
public static class AlertsAdminEndpoints
{
    /// <summary>Errors before warnings, and nothing else decides the order.</summary>
    private const int ErrorRank = 0;
    private const int WarningRank = 1;

    private const char PathRoot = '/';

    public static RouteGroupBuilder MapAlertsAdmin(this RouteGroupBuilder group)
    {
        group.MapGet("/alerts", ListAsync).WithName("ListAlerts");
        return group;
    }

    private static async Task<IReadOnlyList<AlertResponse>> ListAsync(
        IEnumerable<IOperatorAlertSource> sources,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var logger = loggers.CreateLogger(typeof(AlertsAdminEndpoints));
        var alerts = new List<OperatorAlert>();

        foreach (var source in sources)
        {
            alerts.AddRange(await source.CollectAsync(cancellationToken));
        }

        return alerts
            .OrderBy(alert => alert.Severity == AlertSeverity.Error ? ErrorRank : WarningRank)
            .Select(alert => Describe(alert, logger))
            .ToList();
    }

    /// <summary>
    /// The contract asserted rather than trusted. A <see cref="OperatorAlert.Link"/> that is
    /// absolute, protocol-relative or not rooted at '/' is answered as null and logged: this value
    /// becomes an href on a page the operator trusts, and one careless source is all it takes.
    /// </summary>
    private static AlertResponse Describe(OperatorAlert alert, ILogger logger)
    {
        var link = alert.Link;

        // "//evil.example" is a URL to another origin that starts with '/', which is why the root
        // check alone is not enough and a second slash disqualifies it.
        if ((link is not null)
            && (!link.StartsWith(PathRoot) || link.StartsWith($"{PathRoot}{PathRoot}")))
        {
            logger.LogWarning(
                "Alert {Code} offered a link that is not a path within this application. Dropped.",
                alert.Code);

            link = null;
        }

        return new AlertResponse(
            alert.Code,
            alert.Severity,
            alert.Subject,
            alert.Since is null ? null : InstantPattern.ExtendedIso.Format(alert.Since.Value),
            link,
            alert.Details);
    }
}
