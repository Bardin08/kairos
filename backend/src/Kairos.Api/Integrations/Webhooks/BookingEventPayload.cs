using System.Text.Json;

using NodaTime;
using NodaTime.Text;

namespace Kairos.Api.Integrations.Webhooks;

/// <summary>
/// The JSON body a subscriber receives, rendered once at enqueue time and signed and delivered
/// verbatim afterwards.
/// </summary>
/// <remarks>
/// It names its own event rather than leaving that to a header, and the reason is the signature:
/// <see cref="WebhookSigner"/> covers the body, so an event name in the body cannot be changed in
/// flight, while one carried only in a header could be. It also makes a captured delivery
/// self-describing, which is the difference between a debuggable log line and a bare object.
///
/// The instants are ISO-8601 strings rather than the Unix seconds the database stores. A webhook
/// body is read by other people's code, and a number that needs a comment to interpret is a
/// worse contract than a string that does not.
/// </remarks>
public sealed record BookingEventPayload(
    string Event,
    Guid BookingId,
    string Slug,
    string Start,
    string End,
    int DurationMinutes)
{
    public const string BookingCreated = "booking.created";

    public const string BookingCancelled = "booking.cancelled";

    public const string MeetingEnded = "meeting.ended";

    public static BookingEventPayload Created(
        Guid bookingId,
        string slug,
        Instant start,
        Duration duration) =>
        For(BookingCreated, bookingId, slug, start, start + duration);

    public static BookingEventPayload Cancelled(
        Guid bookingId,
        string slug,
        Instant start,
        Instant end) =>
        For(BookingCancelled, bookingId, slug, start, end);

    /// <summary>
    /// Announced after the fact, and still the same six fields. A shape designed for creation
    /// turned out to fit: the times are the meeting's own, not the moment of the announcement,
    /// so a subscriber joining <c>booking.created</c> to <c>meeting.ended</c> compares like with
    /// like. Adding an <c>EndedAt</c> would only restate <see cref="End"/> with worse precision —
    /// the poller notices a meeting up to a tick after it ends, and that lag is the poller's, not
    /// the meeting's.
    /// </summary>
    public static BookingEventPayload Ended(
        Guid bookingId,
        string slug,
        Instant start,
        Instant end) =>
        For(MeetingEnded, bookingId, slug, start, end);

    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>
    /// The one place a booking becomes a body. Three events with three renderings would be three
    /// chances for the field set to drift, and a subscriber that has to special-case an event to
    /// read its start time is reading a worse contract than one that never has to.
    /// </summary>
    private static BookingEventPayload For(
        string @event,
        Guid bookingId,
        string slug,
        Instant start,
        Instant end) =>
        new(
            @event,
            bookingId,
            slug,
            InstantPattern.ExtendedIso.Format(start),
            InstantPattern.ExtendedIso.Format(end),
            (int)(end - start).TotalMinutes);
}
