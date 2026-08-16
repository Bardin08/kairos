namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// One length a link is offered at. The key is the pair, because offering 30 minutes twice on
/// the same link is not a second row — it is the same row saved again.
/// </summary>
public sealed class LinkDuration
{
    public Guid LinkId { get; set; }

    public Link Link { get; set; } = null!;

    public int Minutes { get; set; }

    /// <summary>The order the booking page lists them in. Not the same as ascending length.</summary>
    public int SortOrder { get; set; }
}
