using NodaTime;

namespace Kairos.Api.Integrations.Email;

/// <summary>
/// Everything one VEVENT needs. <paramref name="Sequence"/> is the caller's to advance:
/// a reschedule or a cancellation that reuses the previous number is an update Outlook drops
/// on the floor, and the builder cannot know what the previous number was.
/// </summary>
public sealed record IcsRequest(
    Guid BookingId,
    Instant Start,
    Instant End,
    string Summary,
    string Description,
    string OrganizerEmail,
    string AttendeeEmail,
    int Sequence,
    IcsMethod Method);
