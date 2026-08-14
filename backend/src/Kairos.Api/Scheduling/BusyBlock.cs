namespace Kairos.Api.Scheduling;

/// <summary>A busy interval and the margin the meeting that produced it demands around itself.</summary>
public readonly record struct BusyBlock(Interval Interval, int MarginMinutes);
