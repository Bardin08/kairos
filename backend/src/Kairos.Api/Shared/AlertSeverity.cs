namespace Kairos.Api.Shared;

/// <summary>
/// How loudly an alert asks. Two values, so a source cannot invent a third that no screen knows
/// how to draw.
/// </summary>
public static class AlertSeverity
{
    /// <summary>Something the operator relies on is not working now.</summary>
    public const string Error = "error";

    /// <summary>Something has degraded or will stop working. Nothing is lost yet.</summary>
    public const string Warning = "warning";
}
