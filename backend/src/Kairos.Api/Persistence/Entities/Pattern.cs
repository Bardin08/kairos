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
    /// Raised by whoever replaces a date's windows, and checked by EF on the way out. SQLite has no
    /// rowversion, so the discipline is the writer's: read, decide, save, raising the number read,
    /// or two writers deciding at once both commit and the date holds neither answer. Named for what
    /// it guards — the weekly rules have their own below, deliberately not the same lock.
    /// </summary>
    public int OverridesVersion { get; set; }

    /// <summary>
    /// The same guard for the weekly rules, and the one place the reason it is NOT an EF concurrency
    /// token is written down. EF builds an update's WHERE clause from the key plus every token on the
    /// entity, not only the changed ones, so a second token would join the override save's update:
    /// blocking a holiday would be refused because somebody was editing Tuesday's hours, usually the
    /// same operator. ReplaceRulesAsync raises this column with a conditional UPDATE built from its
    /// own predicate, reading no token metadata. Add IsConcurrencyToken and
    /// ReplaceRulesAndSaveOverride_AtOnce_DoNotRefuseEachOther goes red.
    /// </summary>
    public int RulesVersion { get; set; }

    public ICollection<PatternRule> Rules { get; set; } = [];

    public ICollection<PatternOverride> Overrides { get; set; } = [];
}
