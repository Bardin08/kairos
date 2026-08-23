using NodaTime;

namespace Kairos.Api.Integrations;

/// <summary>
/// Every field is something the booking already knows; nothing here exists because a particular
/// vendor's API asks for it.
/// </summary>
/// <remarks>
/// What is absent is as much of the design as what is present. No calendar id and no organizer:
/// both are the adapter's connected account, and only the adapter knows it. No time zone: a
/// meeting is two instants, and a zone is how the page and the <c>.ics</c> render them. No
/// conference — that is <see cref="IConferenceCreator"/>'s, and the two fail independently.
/// </remarks>
/// <param name="BookingId">The correlation key an adapter is expected to build its own
/// idempotency on.</param>
/// <param name="Start">UTC. Half-open with <paramref name="End"/>, as everywhere else.</param>
/// <param name="Description">The booker's notes, verbatim. Empty is normal.</param>
public sealed record CalendarEventRequest(
    Guid BookingId,
    Instant Start,
    Instant End,
    string Title,
    string Description,
    string AttendeeName,
    string AttendeeEmail);
