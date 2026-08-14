namespace Kairos.Api.Scheduling;

/// <summary>Fully resolved limits. Every field has a value.</summary>
public readonly record struct Limits(
    int MarginMinutes, int MinNoticeMinutes, int DailyCap, int StartIncrementMinutes);
