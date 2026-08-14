namespace Kairos.Api.Scheduling;

/// <summary>
/// The seeded global limits. Referenced by the Settings seed and by every test that needs to
/// know what "the global" is, so the numbers exist once rather than as literals scattered
/// across fixtures.
/// </summary>
public static class SchedulingDefaults
{
    public const int MarginMinutes = 15;
    public const int MinNoticeMinutes = 120;
    public const int DailyCap = 4;
    public const int StartIncrementMinutes = 30;

    /// <summary>
    /// UTC by default. The operator sets a real zone in Settings; nothing in the code assumes
    /// one, so a fresh database is unambiguous rather than quietly Central European.
    /// </summary>
    public const string TimeZoneId = "UTC";
}
