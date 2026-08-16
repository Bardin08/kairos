namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// One window a date offers instead of its weekly rules. Several rows may share a date.
/// A single row with both times null blocks the date; no rows at all means the date keeps
/// the weekly rules. That distinction is the whole reason the null pair still exists.
/// </summary>
public sealed class PatternOverride
{
    /// <summary>UUIDv7.</summary>
    public Guid Id { get; set; }

    public Guid PatternId { get; set; }

    public Pattern Pattern { get; set; } = null!;

    /// <summary>The local date this replaces, in the organizer's zone.</summary>
    public DateOnly Date { get; set; }

    /// <summary>Minutes from local midnight.</summary>
    public int? StartMinute { get; set; }

    public int? EndMinute { get; set; }
}
