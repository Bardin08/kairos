namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// A bookable event type — what the public booking page is for. Availability comes from the
/// pattern; the four nullable columns narrow the globals for this link alone.
/// </summary>
public sealed class Link
{
    /// <summary>UUIDv7.</summary>
    public Guid Id { get; set; }

    /// <summary>The path segment the public URL carries. Unique.</summary>
    public required string Slug { get; set; }

    public required string Name { get; set; }

    public string Description { get; set; } = string.Empty;

    public Guid PatternId { get; set; }

    public Pattern Pattern { get; set; } = null!;

    /// <summary>Inactive links stay bookable for nobody and resolvable for existing bookings.</summary>
    public bool Active { get; set; } = true;

    /// <summary>Null inherits the global. Zero is a deliberate zero — which is why it is nullable.</summary>
    public int? MarginMinutes { get; set; }

    public int? MinNoticeMinutes { get; set; }

    public int? DailyCap { get; set; }

    public int? StartIncrementMinutes { get; set; }

    public ICollection<LinkDuration> Durations { get; set; } = [];
}
