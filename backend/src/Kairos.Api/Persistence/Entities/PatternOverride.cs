namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// Replaces the weekly rules for one date. Null times block the date entirely, which is the
/// common case — a holiday is an override with nothing in it rather than a separate concept.
/// </summary>
public sealed class PatternOverride
{
    /// <summary>UUIDv7.</summary>
    public Guid Id { get; set; }

    public Guid PatternId { get; set; }

    public Pattern Pattern { get; set; } = null!;

    /// <summary>The local date this replaces, in the organizer's zone.</summary>
    public DateOnly Date { get; set; }

    /// <summary>Minutes from local midnight, or null to block the date.</summary>
    public int? StartMinute { get; set; }

    public int? EndMinute { get; set; }
}
