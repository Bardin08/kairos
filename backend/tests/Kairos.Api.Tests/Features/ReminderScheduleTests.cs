using Kairos.Api.Features.Bookings;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The rule that removes this feature's edge cases instead of enumerating them: a reminder whose
/// moment has already passed is never written, so a row that exists was once in the future.
/// </summary>
public class ReminderScheduleTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 9, 14, 10, 0);
    private static readonly Duration Lead = ReminderSchedule.Lead;
    private static readonly Duration OneMinute = Duration.FromMinutes(1);

    private static readonly Instant MeetingStart = Instant.FromUtc(2026, 9, 15, 12, 0);
    private static readonly Instant ExpectedDueAt = Instant.FromUtc(2026, 9, 14, 12, 0);

    /// <summary>
    /// Pinned against instants written independently of <see cref="ReminderSchedule.Lead"/>, so
    /// this is the one test in the class that notices the lead itself changing. Every other test
    /// below builds its instants from that same field, which makes them agree with whatever value
    /// it holds rather than checking it — this fails if the mail stops saying "tomorrow" and means it.
    /// </summary>
    [Fact]
    public void DueAt_IsTwentyFourHoursBeforeTheStart()
    {
        ReminderSchedule.DueAt(MeetingStart).ShouldBe(ExpectedDueAt);
    }

    [Fact]
    public void ShouldSchedule_AMeetingFurtherOutThanTheLead_IsTrue()
    {
        ReminderSchedule.ShouldSchedule(Now + Lead + OneMinute, Now).ShouldBeTrue();
    }

    /// <summary>
    /// The short-notice case. The confirmation they just received IS the reminder, and a second
    /// mail a minute later saying "tomorrow" about a meeting today is untrue as well as annoying.
    /// </summary>
    [Fact]
    public void ShouldSchedule_AMeetingSoonerThanTheLead_IsFalse()
    {
        ReminderSchedule.ShouldSchedule(Now + Lead - OneMinute, Now).ShouldBeFalse();
    }

    /// <summary>
    /// The boundary, named because it is reachable only with a clock a test can fix. A meeting
    /// exactly one lead away has a due moment of exactly now, and now has not passed.
    /// </summary>
    [Fact]
    public void ShouldSchedule_AMeetingExactlyOneLeadAway_IsTrue()
    {
        ReminderSchedule.ShouldSchedule(Now + Lead, Now).ShouldBeTrue();
    }
}
