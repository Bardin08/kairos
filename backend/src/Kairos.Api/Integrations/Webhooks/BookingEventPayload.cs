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

    public static BookingEventPayload Created(
        Guid bookingId,
        string slug,
        Instant start,
        Duration duration) =>
        new(
            BookingCreated,
            bookingId,
            slug,
            InstantPattern.ExtendedIso.Format(start),
            InstantPattern.ExtendedIso.Format(start + duration),
            (int)duration.TotalMinutes);

    public string ToJson() => JsonSerializer.Serialize(this);
}
