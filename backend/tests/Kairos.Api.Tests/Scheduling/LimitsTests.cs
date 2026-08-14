using Kairos.Api.Scheduling;

using Shouldly;

namespace Kairos.Api.Tests.Scheduling;

public class LimitsTests
{
    private const int OverriddenMargin = 30;
    private const int OverriddenDailyCap = 1;

    private static readonly Limits Global = new(
        SchedulingDefaults.MarginMinutes,
        SchedulingDefaults.MinNoticeMinutes,
        SchedulingDefaults.DailyCap,
        SchedulingDefaults.StartIncrementMinutes);

    [Fact]
    public void With_AllOverridesNull_InheritsGlobal() =>
        Global.With(new LimitOverrides(null, null, null, null)).ShouldBe(Global);

    [Fact]
    public void With_ZeroMargin_KeepsExplicitZeroAndInheritsTheRest()
    {
        var resolved = Global.With(new LimitOverrides(MarginMinutes: 0, null, null, null));

        resolved.MarginMinutes.ShouldBe(0);
        resolved.MinNoticeMinutes.ShouldBe(SchedulingDefaults.MinNoticeMinutes);
    }

    [Fact]
    public void With_PartialOverrides_ResolvesEachFieldIndependently()
    {
        var resolved = Global.With(
            new LimitOverrides(OverriddenMargin, null, OverriddenDailyCap, null));

        resolved.MarginMinutes.ShouldBe(OverriddenMargin);
        resolved.MinNoticeMinutes.ShouldBe(SchedulingDefaults.MinNoticeMinutes);
        resolved.DailyCap.ShouldBe(OverriddenDailyCap);
        resolved.StartIncrementMinutes.ShouldBe(SchedulingDefaults.StartIncrementMinutes);
    }
}
