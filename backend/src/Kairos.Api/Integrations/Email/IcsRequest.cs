using NodaTime;

namespace Kairos.Api.Integrations.Email;

/// <summary>
/// <paramref name="Sequence"/> is the caller's to advance: a reschedule or a cancellation that
/// reuses the previous number is an update Outlook drops on the floor, and the builder cannot
/// know what the previous number was.
/// </summary>
/// <param name="Uid">
/// RFC 5545 UID. Comes from <see cref="MeetingIdentity"/> rather than being derived here: which
/// identity a meeting carries is a decision about the provider that placed it, not about
/// iCalendar.
/// </param>
/// <param name="SentBy">
/// The address posting on the organizer's behalf, or null when the organizer posts for itself.
/// </param>
/// <param name="Location">
/// Where to join, or null when the meeting has no conference. Never an empty string: a client
/// renders an empty LOCATION as a blank location field, which reads as a place nobody knows.
/// </param>
public sealed record IcsRequest(
    string Uid,
    Instant Start,
    Instant End,
    string Summary,
    string Description,
    string OrganizerEmail,
    string AttendeeEmail,
    int Sequence,
    IcsMethod Method,
    string? Location = null,
    string? SentBy = null);
