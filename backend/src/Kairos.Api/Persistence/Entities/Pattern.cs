namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// A named, reusable availability schedule: the weekly windows plus the dates that deviate from
/// them. Links point at a pattern rather than carrying their own hours, so "I no longer work
/// Friday afternoons" is one edit instead of one per link.
/// </summary>
public sealed class Pattern
{
    /// <summary>UUIDv7.</summary>
    public Guid Id { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// Bumped by whoever replaces a date's windows, and checked by EF on the way out. SQLite has
    /// no rowversion, so the column is explicit and the discipline is the writer's: a write that
    /// reads the pattern, decides, and then saves has to raise the number it read, or two of them
    /// deciding at once both commit and the date ends up holding neither operator's answer.
    /// </summary>
    public int Version { get; set; }

    public ICollection<PatternRule> Rules { get; set; } = [];

    public ICollection<PatternOverride> Overrides { get; set; } = [];
}
