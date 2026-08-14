using Kairos.Api.Scheduling;

using NodaTime;

namespace Kairos.Api.Availability;

/// <summary>
/// One booking-page query: the slots, and everything the query had to resolve to produce them.
/// The resolved fields are returned rather than recomputed by the caller because the create path
/// re-checks availability through the same call and then writes with
/// <c>BookingWriter.TryCreate(linkId, ..., queryingMargin)</c>. Resolving the link and its limits
/// a second time in the endpoint would be the same eight lines on the two paths that must never
/// disagree, and when they drift the page offers a slot the writer refuses.
/// </summary>
/// <param name="LinkId">The resolved link. The slug is public; this is what writes reference.</param>
/// <param name="DurationMinutes">The duration actually used — the requested one, or the only one a single-duration link offers.</param>
/// <param name="OfferedDurations">Every duration the link offers, in the order the booking page should list them.</param>
/// <param name="Limits">Global limits with the link's overrides applied. <c>Limits.MarginMinutes</c> is <c>queryingMargin</c>.</param>
/// <param name="Starts">UTC slot starts, ascending. Empty is a normal answer, not a failure.</param>
public readonly record struct SlotResults(
    Guid LinkId,
    int DurationMinutes,
    IReadOnlyList<int> OfferedDurations,
    Limits Limits,
    IReadOnlyList<Instant> Starts);
