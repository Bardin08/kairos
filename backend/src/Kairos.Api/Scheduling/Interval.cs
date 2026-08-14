using NodaTime;

namespace Kairos.Api.Scheduling;

/// <summary>Half-open UTC interval [Start, End). Touching intervals do not overlap.</summary>
public readonly record struct Interval(Instant Start, Instant End)
{
    /// <remarks><c>in</c> because this is the slot generator's inner loop.</remarks>
    public bool Overlaps(in Interval other) => Start < other.End && other.Start < End;

    public Interval Pad(Duration by) => new(Start - by, End + by);
}
