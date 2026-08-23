namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// A person on a booking. Extracted from Booking so a second participant is a row rather than a
/// migration; today exactly one is written, with the Booker role.
/// </summary>
public sealed class BookingParticipant
{
    public Guid Id { get; set; }

    public Guid BookingId { get; set; }

    public Booking Booking { get; set; } = null!;

    public ParticipantRole Role { get; set; }

    public required string Name { get; set; }

    public required string Email { get; set; }

    /// <summary>The IANA zone this person saw the times in. Not the organizer's.</summary>
    public string TimeZoneId { get; set; } = string.Empty;
}
