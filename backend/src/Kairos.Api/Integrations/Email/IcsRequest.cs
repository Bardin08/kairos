using NodaTime;

namespace Kairos.Api.Integrations.Email;

/// <summary>
/// <paramref name="Sequence"/> is the caller's to advance: a reschedule or a cancellation that
/// reuses the previous number is an update Outlook drops on the floor, and the builder cannot
/// know what the previous number was.
/// </summary>
/// <param name="Location">
/// Where to join, or null when the meeting has no conference. Never an empty string: a client
/// renders an empty LOCATION as a blank location field, which reads as a place nobody knows.
/// </param>
public sealed record IcsRequest(
    Guid BookingId,
    Instant Start,
    Instant End,
    string Summary,
    string Description,
    string OrganizerEmail,
    string AttendeeEmail,
    int Sequence,
    IcsMethod Method,
    string? Location = null);
