using Kairos.Api.Scheduling;

using NodaTime;

namespace Kairos.Api.Availability;

/// <summary>
/// One booking-page query: the slots, and everything resolved to produce them. Returned rather
/// than recomputed because the create path re-checks availability through this same call, then
/// writes with <c>BookingWriter.TryCreate(linkId, ..., queryingMargin)</c>. Resolving twice
/// duplicates eight lines across two paths that must never disagree.
/// </summary>
/// <param name="LinkId">The resolved link. The slug is public; this is what writes reference.</param>
/// <param name="DurationMinutes">The requested one, or the only one a single-duration link offers.</param>
/// <param name="OfferedDurations">In the order the booking page should list them.</param>
/// <param name="Limits">Globals with the link's overrides applied; <c>MarginMinutes</c> is <c>queryingMargin</c>.</param>
/// <param name="Starts">UTC slot starts, ascending. Empty is a normal answer, not a failure.</param>
public readonly record struct SlotResults(
    Guid LinkId,
    int DurationMinutes,
    IReadOnlyList<int> OfferedDurations,
    Limits Limits,
    IReadOnlyList<Instant> Starts);
