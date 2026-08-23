using System.Text.Json;

using Kairos.Api.Integrations.Webhooks;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Integrations;

/// <summary>
/// The body other people's code reads, and the part of it that is a contract rather than an
/// implementation detail: the three event names, and that every event renders the same six
/// fields. A subscriber special-casing an event to find its start time reads a worse contract.
/// </summary>
public class BookingEventPayloadTests
{
    private const string Slug = "consulting";
    private const int DurationMinutes = 25;
    private const long MeetingStartUtc = 1_788_000_000;

    private static readonly Instant Start = Instant.FromUnixTimeSeconds(MeetingStartUtc);
    private static readonly Instant End = Start + Duration.FromMinutes(DurationMinutes);

    [Fact]
    public void EventNames_AreTheThreeTheSpecNames() =>
        new[] { BookingEventPayload.BookingCreated, BookingEventPayload.BookingCancelled, BookingEventPayload.MeetingEnded }
            .ShouldBe(["booking.created", "booking.cancelled", "meeting.ended"]);

    [Theory]
    [InlineData(BookingEventPayload.BookingCancelled)]
    [InlineData(BookingEventPayload.MeetingEnded)]
    public void AfterTheFactEvents_CarryTheMeetingsOwnTimes(string expectedEvent)
    {
        var bookingId = Guid.CreateVersion7();
        var payload = expectedEvent == BookingEventPayload.MeetingEnded
            ? BookingEventPayload.Ended(bookingId, Slug, Start, End)
            : BookingEventPayload.Cancelled(bookingId, Slug, Start, End);

        payload.Event.ShouldBe(expectedEvent);
        payload.BookingId.ShouldBe(bookingId);
        payload.Slug.ShouldBe(Slug);
        payload.Start.ShouldBe("2026-08-29T10:40:00Z");
        payload.End.ShouldBe("2026-08-29T11:05:00Z");

        // The meeting's length, not the age of the announcement. meeting.ended is noticed up to
        // a tick after the fact, and that lag belongs to the poller rather than to the payload.
        payload.DurationMinutes.ShouldBe(DurationMinutes);
    }

    /// <summary>
    /// The signed body is this string, byte for byte, so the property names are as much of the
    /// contract as the values are. Nothing re-serialises it between here and the wire.
    /// </summary>
    [Fact]
    public void ToJson_NamesItsOwnEvent_InThePartOfTheDeliveryTheSignatureCovers()
    {
        var json = BookingEventPayload.Ended(Guid.CreateVersion7(), Slug, Start, End).ToJson();

        JsonDocument.Parse(json).RootElement.GetProperty(nameof(BookingEventPayload.Event))
            .GetString().ShouldBe(BookingEventPayload.MeetingEnded);
    }
}
