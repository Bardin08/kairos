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

    [Fact]
    public void DueAt_IsTheStartLessTheLead()
    {
        var start = Now + Lead + OneMinute;

        ReminderSchedule.DueAt(start).ShouldBe(start - Lead);
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
