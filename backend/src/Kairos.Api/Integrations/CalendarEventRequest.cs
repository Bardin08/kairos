using NodaTime;

namespace Kairos.Api.Integrations;

/// <summary>
/// One meeting, described the way a booking describes it, for whoever is going to put it in a
/// calendar. Every field is something the booking already knows; nothing here exists because a
/// particular vendor's API asks for it.
/// </summary>
/// <remarks>
/// What is deliberately absent is as much of the design as what is present. There is no calendar
/// id, because a booking has no opinion about which calendar it lands in — that is the adapter's
/// account, and the adapter is the only thing that knows it. There is no organizer, for the same
/// reason: the connected account <em>is</em> the organizer, so passing it would be a field every
/// adapter ignores. There is no time zone, because a meeting is two instants; a zone is how
/// somebody reads them, which belongs to the page and to the <c>.ics</c>, not to a write. And
/// there is no conference of any kind — that is <see cref="IConferenceCreator"/>'s job, and the
/// two fail independently.
/// </remarks>
/// <param name="BookingId">Kairos's identifier for the meeting, and the correlation key an
/// adapter is expected to build its own idempotency on.</param>
/// <param name="Start">UTC. Half-open with <paramref name="End"/>, as everywhere else.</param>
/// <param name="Title">What the organizer will read in their own calendar.</param>
/// <param name="Description">The booker's notes, verbatim. Empty is normal.</param>
public sealed record CalendarEventRequest(
    Guid BookingId,
    Instant Start,
    Instant End,
    string Title,
    string Description,
    string AttendeeName,
    string AttendeeEmail);
