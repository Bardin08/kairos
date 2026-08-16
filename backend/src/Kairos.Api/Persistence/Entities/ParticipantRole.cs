namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// Numbered with gaps for the same reason as <see cref="BookingStatus"/>. Part A only ever
/// writes <see cref="Booker"/>; the second value exists so group bookings are a row rather than
/// a schema change.
/// </summary>
public enum ParticipantRole
{
    Booker = 10,
    Invitee = 20,
}
