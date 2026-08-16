using Kairos.Api.Scheduling;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Scheduling;

public class AllocationTests
{
    private const int UnlimitedDailyCap = 99;
    private const int DaysGenerated = 30;
    private const int AllocationBudgetBytes = 64 * 1024;

    private const int WindowStartHour = 9;
    private const int WindowEndHour = 17;

    /// <summary>A twelve-hour day at a quarter-hour increment: both are ordinary settings.</summary>
    private const int LongestMonthDays = 31;
    private const int LongDayStartHour = 8;
    private const int LongDayEndHour = 20;
    private const int QuarterHour = 15;

    private static readonly DateTimeZone Utc =
        DateTimeZoneProviders.Tzdb[SchedulingDefaults.TimeZoneId];
    private static readonly Dictionary<LocalDate, int> NoBookingsYet = [];

    private static long Measure(int days, int startHour, int endHour, int step)
    {
        var start = new LocalDate(2026, 9, 1);
        var windows = Enumerable.Range(0, days)
            .Select(offset => new LocalWindow(
                start.PlusDays(offset),
                new LocalTime(startHour, 0),
                new LocalTime(endHour, 0)))
            .ToArray();

        var generator = new SlotGenerator();
        var limits = new Limits(
            SchedulingDefaults.MarginMinutes,
            MinNoticeMinutes: 0,
            DailyCap: UnlimitedDailyCap,
            step);
        var now = Instant.FromUtc(2026, 1, 1, 0, 0);
        var slot = Duration.FromMinutes(step);

        // Warm up first: the measured run must not include JIT and first-call allocations.
        generator.Generate(windows, [], slot, limits, now, Utc, NoBookingsYet);

        var before = GC.GetAllocatedBytesForCurrentThread();
        generator.Generate(windows, [], slot, limits, now, Utc, NoBookingsYet);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void Generate_AMonthOfSlots_StaysUnderTheAllocationBudget()
    {
        var allocated = Measure(
            DaysGenerated, WindowStartHour, WindowEndHour, SchedulingDefaults.StartIncrementMinutes);

        // Roughly 480 slots. The result list dominates; the candidates must not allocate.
        // If this fails, the struct reasoning in the plan was wrong and the plan is what changes.
        allocated.ShouldBeLessThan(AllocationBudgetBytes);
    }

    /// <summary>
    /// The budget scales with candidates, not with days, and the seeded defaults are its
    /// cheapest configuration. A twelve-hour day at a quarter-hour increment is three times the
    /// work and both halves of it are settings an operator can choose, so the fixture that only
    /// measured 30 days of 09:00-17:00 at 30 minutes was measuring the one case that passed.
    /// </summary>
    [Fact]
    public void Generate_ALongMonthOfShortIncrements_StaysUnderTheAllocationBudget() =>
        Measure(LongestMonthDays, LongDayStartHour, LongDayEndHour, QuarterHour)
            .ShouldBeLessThan(AllocationBudgetBytes);
}
