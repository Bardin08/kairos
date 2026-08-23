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
    /// Raised by whoever replaces a date's windows, and checked by EF on the way out. SQLite has
    /// no rowversion, so the column is explicit and the discipline is the writer's: a write that
    /// reads the pattern, decides, and then saves has to raise the number it read, or two of them
    /// deciding at once both commit and the date ends up holding neither operator's answer. Named
    /// for what it guards — it says nothing about the weekly rules, which have their own below and
    /// are deliberately not the same lock.
    /// </summary>
    public int OverridesVersion { get; set; }

    /// <summary>
    /// The same guard for the weekly rules, and the one place the reason it is NOT an EF
    /// concurrency token is written down. EF builds an update's WHERE clause from the key plus
    /// every token on the entity, not only the tokens that changed, so a second token here would
    /// join the override save's update as well: blocking a holiday would start being refused
    /// because somebody was editing Tuesday's hours, on a workbench where both edits are usually
    /// the same operator's. ReplaceRulesAsync raises this column with a conditional UPDATE
    /// instead, which is generated from its own predicate and reads no token metadata at all.
    /// Add IsConcurrencyToken here and ReplaceRulesAndSaveOverride_AtOnce_DoNotRefuseEachOther
    /// goes red, which is what that test is for.
    /// </summary>
    public int RulesVersion { get; set; }

    public ICollection<PatternRule> Rules { get; set; } = [];

    public ICollection<PatternOverride> Overrides { get; set; } = [];
}
