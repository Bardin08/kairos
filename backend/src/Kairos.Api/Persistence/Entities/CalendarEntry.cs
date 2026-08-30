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

    /// <summary>
    /// The identity the provider minted, quoted by every notice that carries a calendar part.
    /// Null for a booking made before this column existed, for a write that failed, and for a
    /// provider that exposes no UID — all three fall back to the booking id.
    /// </summary>
    public string? CalendarUid { get; set; }

    public CalendarSyncState State { get; set; }

    public string? LastError { get; set; }

    public long UpdatedAt { get; set; }
}
