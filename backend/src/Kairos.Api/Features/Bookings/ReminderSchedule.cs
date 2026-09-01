using NodaTime;

namespace Kairos.Api.Features.Bookings;

/// <summary>
/// When a booking's reminder is due, and whether it gets one.
/// </summary>
/// <remarks>
/// One place, because three callers ask the same question — the write, the move, and the tests
/// that pin the boundary. Two copies of this would drift into a booking whose reminder was
/// scheduled by one rule and deleted by another.
/// </remarks>
public static class ReminderSchedule
{
    /// <summary>
    /// How far ahead of the meeting the invitee is reminded. Fixed, and deliberately not
    /// configurable: a second offset is a second row, a second decision and a control on a
    /// booking form that nobody asked for.
    /// </summary>
    public static readonly Duration Lead = Duration.FromHours(24);

    public static Instant DueAt(Instant start) => start - Lead;

    /// <summary>
    /// Whether to write a row at all. False means the moment has already passed, and the mail the
    /// caller is about to send — a confirmation, or a reschedule — is the notice instead.
    /// </summary>
    public static bool ShouldSchedule(Instant start, Instant now) => DueAt(start) >= now;
}
