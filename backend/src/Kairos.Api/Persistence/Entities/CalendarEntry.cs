namespace Kairos.Api.Persistence.Entities;

/// <summary>Where a booking was written. Not how to join it — that is <see cref="ConferenceLink"/>.</summary>
public sealed class CalendarEntry
{
    public Guid Id { get; set; }

    public Guid BookingId { get; set; }

    public Booking Booking { get; set; } = null!;

    public IntegrationProvider Provider { get; set; }

    /// <summary>Null until a write succeeds. Present with Failed means we tried and know why.</summary>
    public string? ExternalEventId { get; set; }

    public CalendarSyncState State { get; set; }

    public string? LastError { get; set; }

    public long UpdatedAt { get; set; }
}
