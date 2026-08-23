namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// Numbered with gaps for the same reason as <see cref="BookingStatus"/>. Only
/// <see cref="Booker"/> is written today; the second value exists so group bookings are a row
/// rather than a schema change.
/// </summary>
public enum ParticipantRole
{
    Booker = 10,
    Invitee = 20,
}
