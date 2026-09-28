namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// A date that does not follow its weekly rules. Its windows are what the date offers instead, so
/// a date with none is blocked, and a date with no row keeps the weekly rules.
/// </summary>
public sealed class PatternOverride
{
    /// <summary>UUIDv7.</summary>
    public Guid Id { get; set; }

    public Guid PatternId { get; set; }

    public Pattern Pattern { get; set; } = null!;

    /// <summary>The local date this replaces, in the organizer's zone.</summary>
    public DateOnly Date { get; set; }

    public ICollection<OverrideWindow> Windows { get; set; } = [];
}
