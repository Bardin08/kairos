namespace Kairos.Api.Persistence.Entities;

/// <summary>One window a date offers. Several on one date describe a split day.</summary>
public sealed class OverrideWindow
{
    /// <summary>UUIDv7.</summary>
    public Guid Id { get; set; }

    public Guid PatternOverrideId { get; set; }

    public PatternOverride PatternOverride { get; set; } = null!;

    /// <summary>Minutes from local midnight.</summary>
    public int StartMinute { get; set; }

    public int EndMinute { get; set; }
}
