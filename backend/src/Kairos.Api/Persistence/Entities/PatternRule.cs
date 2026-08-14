namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// One weekly window inside a pattern. Several rules on the same day are allowed and describe a
/// split day — mornings and late afternoons with a gap in between.
/// </summary>
public sealed class PatternRule
{
    /// <summary>UUIDv7.</summary>
    public Guid Id { get; set; }

    public Guid PatternId { get; set; }

    public Pattern Pattern { get; set; } = null!;

    /// <summary>ISO-8601 day of week: 1 is Monday, 7 is Sunday. Not <see cref="DayOfWeek"/>, whose Sunday is 0.</summary>
    public int IsoDayOfWeek { get; set; }

    /// <summary>Minutes from local midnight. Integers rather than times, so the window is arithmetic.</summary>
    public int StartMinute { get; set; }

    public int EndMinute { get; set; }
}
