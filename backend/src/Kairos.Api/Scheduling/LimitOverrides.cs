namespace Kairos.Api.Scheduling;

/// <summary>Null means inherit. Zero means deliberately zero. They are different values.</summary>
public readonly record struct LimitOverrides(
    int? MarginMinutes, int? MinNoticeMinutes, int? DailyCap, int? StartIncrementMinutes);
