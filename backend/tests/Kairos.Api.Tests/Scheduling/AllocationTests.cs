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

    private static readonly DateTimeZone Utc =
        DateTimeZoneProviders.Tzdb[SchedulingDefaults.TimeZoneId];
    private static readonly Dictionary<LocalDate, int> NoBookingsYet = [];

    [Fact]
    public void Generate_AMonthOfSlots_StaysUnderTheAllocationBudget()
    {
        var start = new LocalDate(2026, 9, 1);
        var windows = Enumerable.Range(0, DaysGenerated)
            .Select(offset => new LocalWindow(
                start.PlusDays(offset),
                new LocalTime(WindowStartHour, 0),
                new LocalTime(WindowEndHour, 0)))
            .ToArray();

        var generator = new SlotGenerator();
        var limits = new Limits(
            SchedulingDefaults.MarginMinutes,
            MinNoticeMinutes: 0,
            DailyCap: UnlimitedDailyCap,
            SchedulingDefaults.StartIncrementMinutes);
        var now = Instant.FromUtc(2026, 1, 1, 0, 0);
        var slot = Duration.FromMinutes(SchedulingDefaults.StartIncrementMinutes);

        // Warm up first: the measured run must not include JIT and first-call allocations.
        generator.Generate(windows, [], slot, limits, now, Utc, NoBookingsYet);

        var before = GC.GetAllocatedBytesForCurrentThread();
        generator.Generate(windows, [], slot, limits, now, Utc, NoBookingsYet);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Roughly 480 slots. The result list dominates; the candidates must not allocate.
        // If this fails, the struct reasoning in the plan was wrong and the plan is what changes.
        allocated.ShouldBeLessThan(AllocationBudgetBytes);
    }
}
