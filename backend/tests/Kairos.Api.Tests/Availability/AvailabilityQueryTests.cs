using Kairos.Api.Availability;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Scheduling;
using Kairos.Api.Shared;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Availability;

/// <summary>
/// The expansion rules, which live nowhere else: a weekly rule lands on its ISO day and only its
/// ISO day, and an override replaces the day rather than joining it.
/// </summary>
public class AvailabilityQueryTests
{
    private const int MondayIso = 1;
    private const int WednesdayIso = 3;
    private const int MinutesPerHour = 60;
    private const int NineAm = 9 * MinutesPerHour;
    private const int Noon = 12 * MinutesPerHour;
    private const int TwoPm = 14 * MinutesPerHour;
    private const int FivePm = 17 * MinutesPerHour;

    private static readonly Guid PatternId = Guid.CreateVersion7();
    private static readonly LocalDate Monday = new(2026, 9, 14);
    private static readonly LocalDate Wednesday = new(2026, 9, 16);
    private static readonly LocalDate Sunday = new(2026, 9, 20);

    [Fact]
    public async Task GetWindowsAsync_WeeklyRules_LandOnMatchingIsoDaysOnly()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        Seed(context, Rule(MondayIso, NineAm, Noon), Rule(WednesdayIso, TwoPm, FivePm));

        var windows = await new AvailabilityQuery(context)
            .GetWindowsAsync(PatternId, Monday, Sunday, TestContext.Current.CancellationToken);

        LocalWindow[] expected =
        [
            new(Monday, new LocalTime(9, 0), new LocalTime(12, 0)),
            new(Wednesday, new LocalTime(14, 0), new LocalTime(17, 0)),
        ];
        windows.ShouldBe(expected);
    }

    [Fact]
    public async Task GetWindowsAsync_TwoRulesOnOneDay_ExpandToTwoWindows()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        Seed(context, Rule(MondayIso, NineAm, Noon), Rule(MondayIso, TwoPm, FivePm));

        var windows = await new AvailabilityQuery(context)
            .GetWindowsAsync(PatternId, Monday, Monday, TestContext.Current.CancellationToken);

        windows.Length.ShouldBe(2);
        windows.ShouldAllBe(window => window.Date == Monday);
    }

    [Fact]
    public async Task GetWindowsAsync_DateHasAnOverrideWithTimes_ReplacesThatDaysRulesEntirely()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        Seed(context, Rule(MondayIso, NineAm, Noon), Rule(MondayIso, TwoPm, FivePm));
        AddOverride(context, Monday, (NineAm, NineAm + MinutesPerHour));

        var windows = await new AvailabilityQuery(context)
            .GetWindowsAsync(PatternId, Monday, Monday, TestContext.Current.CancellationToken);

        // One window, not three: the override is the day, not an addition to it.
        LocalWindow[] expected = [new(Monday, new LocalTime(9, 0), new LocalTime(10, 0))];
        windows.ShouldBe(expected);
    }

    [Fact]
    public async Task GetWindowsAsync_DateHasTwoWindows_ReturnsBothInsteadOfTheWeeklyRule()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        Seed(context, Rule(WednesdayIso, NineAm, FivePm));
        AddOverride(context, Wednesday, (NineAm, Noon), (TwoPm, FivePm));

        var windows = await new AvailabilityQuery(context)
            .GetWindowsAsync(PatternId, Wednesday, Wednesday, TestContext.Current.CancellationToken);

        LocalWindow[] expected =
        [
            new(Wednesday, new LocalTime(9, 0), new LocalTime(12, 0)),
            new(Wednesday, new LocalTime(14, 0), new LocalTime(17, 0)),
        ];
        windows.ShouldBe(expected);
    }

    [Fact]
    public async Task GetWindowsAsync_WindowsStoredAfternoonFirst_ComeBackMorningFirst()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        Seed(context, Rule(WednesdayIso, NineAm, FivePm));
        AddOverride(context, Wednesday, (TwoPm, FivePm), (NineAm, Noon));

        var windows = await new AvailabilityQuery(context)
            .GetWindowsAsync(PatternId, Wednesday, Wednesday, TestContext.Current.CancellationToken);

        windows.Select(window => window.Start).ShouldBe([new LocalTime(9, 0), new LocalTime(14, 0)]);
    }

    [Fact]
    public async Task GetWindowsAsync_OverrideWithNoWindows_BlocksTheDayAndLeavesTheRestAlone()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        Seed(context, Rule(MondayIso, NineAm, Noon), Rule(WednesdayIso, TwoPm, FivePm));
        AddOverride(context, Monday);

        var windows = await new AvailabilityQuery(context)
            .GetWindowsAsync(PatternId, Monday, Sunday, TestContext.Current.CancellationToken);

        LocalWindow[] expected = [new(Wednesday, new LocalTime(14, 0), new LocalTime(17, 0))];
        windows.ShouldBe(expected);
    }

    [Fact]
    public async Task GetWindowsAsync_OverrideOnADayWithNoWeeklyRules_OpensThatDay()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        Seed(context, Rule(MondayIso, NineAm, Noon));

        // "I am free this one Sunday" — an override that adds a day rather than trimming one.
        // Reading overrides only where rules already exist would drop it silently.
        AddOverride(context, Sunday, (TwoPm, FivePm));

        var windows = await new AvailabilityQuery(context)
            .GetWindowsAsync(PatternId, Monday, Sunday, TestContext.Current.CancellationToken);

        LocalWindow[] expected =
        [
            new(Monday, new LocalTime(9, 0), new LocalTime(12, 0)),
            new(Sunday, new LocalTime(14, 0), new LocalTime(17, 0)),
        ];
        windows.ShouldBe(expected);
    }

    [Fact]
    public async Task GetWindowsAsync_RuleRunsToMinute1440_ThrowsInsteadOfBuildingAnImpossibleTime()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();

        // The natural way to write "until midnight", and there is no CHECK stopping it. LocalTime
        // has no 24:00, so the alternative to this throw is an ArgumentOutOfRangeException from
        // NodaTime on a customer-facing page.
        Seed(context, Rule(MondayIso, TwoPm, 24 * MinutesPerHour));

        var thrown = await Should.ThrowAsync<DomainException>(() => new AvailabilityQuery(context)
            .GetWindowsAsync(PatternId, Monday, Monday, TestContext.Current.CancellationToken));

        thrown.Code.ShouldBe(AvailabilityQuery.CorruptPatternCode);
    }

    private static PatternRule Rule(int isoDayOfWeek, int startMinute, int endMinute) => new()
    {
        Id = Guid.CreateVersion7(),
        PatternId = PatternId,
        IsoDayOfWeek = isoDayOfWeek,
        StartMinute = startMinute,
        EndMinute = endMinute,
    };

    private static void AddOverride(KairosDbContext context, LocalDate date, params (int Start, int End)[] windows)
    {
        context.PatternOverrides.Add(new PatternOverride
        {
            Id = Guid.CreateVersion7(),
            PatternId = PatternId,
            Date = date.ToDateOnly(),
            Windows =
            [
                .. windows.Select(window => new OverrideWindow
                {
                    Id = Guid.CreateVersion7(),
                    StartMinute = window.Start,
                    EndMinute = window.End,
                }),
            ],
        });
        context.SaveChanges();
    }

    private static void Seed(KairosDbContext context, params PatternRule[] rules)
    {
        context.Patterns.Add(new Pattern { Id = PatternId, Name = "Weekdays" });
        context.PatternRules.AddRange(rules);
        context.SaveChanges();
    }
}
