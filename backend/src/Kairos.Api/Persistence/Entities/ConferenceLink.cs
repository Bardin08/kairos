namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// How to join a booking. Separate from <see cref="CalendarEntry"/> because the two fail
/// independently: a calendar write can succeed while conferencing is unavailable, and a
/// conference link outlives whichever calendar happened to carry it.
/// </summary>
public sealed class ConferenceLink
{
    public Guid Id { get; set; }

    public Guid BookingId { get; set; }

    public Booking Booking { get; set; } = null!;

    public IntegrationProvider Provider { get; set; }

    /// <summary>The provider's own handle for the conference. Not the URL.</summary>
    public required string ExternalId { get; set; }

    public required string JoinUrl { get; set; }

    public long CreatedAt { get; set; }
}
