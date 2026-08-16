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

    public ICollection<PatternRule> Rules { get; set; } = [];

    public ICollection<PatternOverride> Overrides { get; set; } = [];
}
