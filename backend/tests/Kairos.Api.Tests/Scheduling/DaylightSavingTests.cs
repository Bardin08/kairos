using Kairos.Api.Scheduling;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Scheduling;

/// <summary>
/// Candidate starts are walked in local wall time and only then mapped, which is the only reason
/// these come out right. Walking UTC would shift every slot on the far side of a transition.
/// </summary>
public class DaylightSavingTests
{
    private const int NoMargin = 0;
    private const int NoNotice = 0;
    private const int UnlimitedDailyCap = 99;
    private const int HalfHour = 30;

    /// <summary>Both transitions happen overnight, so the windows under test are nocturnal.</summary>
    private const int NightStartHour = 1;
    private const int NightEndHour = 5;

    /// <summary>01:00 and 01:30 exist, 02:00 and 02:30 do not, 03:00 through 04:30 exist.</summary>
    private const int SlotsOnTheSpringForwardNight = 6;
    private const int SlotsOnAnOrdinaryNight = 8;
    private const int SlotsBeforeTheGap = 2;
    private const int FirstHourAfterTheGap = 3;

    /// <summary>The hour 02:00 to 03:00 local is lived twice on the fall-back night.</summary>
    private const int RepeatedHour = 2;

    private const int MorningHour = 9;
    private const int CestOffsetHours = 2;
    private const int CetOffsetHours = 1;

    /// <remarks>
    /// Declared here rather than taken from SchedulingDefaults: the default is UTC, and UTC has
    /// no transitions. Warsaw is the organizer's real zone and shifts twice a year, which is the
    /// only reason this file names a zone at all.
    /// </remarks>
    private static readonly DateTimeZone Warsaw = DateTimeZoneProviders.Tzdb["Europe/Warsaw"];

    private static readonly LocalDate SpringForward = new(2026, 3, 29);
    private static readonly LocalDate OrdinaryNight = new(2026, 3, 22);
    private static readonly LocalDate FallBack = new(2026, 10, 25);
    private static readonly LocalDate BeforeFallBack = new(2026, 10, 24);
    private static readonly LocalDate AfterFallBack = new(2026, 10, 26);

    private static readonly Limits NoLimits = new(NoMargin, NoNotice, UnlimitedDailyCap, HalfHour);
    private static readonly Instant LongBefore = Instant.FromUtc(2026, 1, 1, 0, 0);
    private static readonly Dictionary<LocalDate, int> NoBookingsYet = [];

    private static IReadOnlyList<Instant> Generate(LocalWindow window) =>
        new SlotGenerator().Generate(
            [window],
            [],
            Duration.FromMinutes(HalfHour),
            NoLimits,
            LongBefore,
            Warsaw,
            NoBookingsYet);

    private static LocalWindow Night(LocalDate date) =>
        new(date, new LocalTime(NightStartHour, 0), new LocalTime(NightEndHour, 0));

    private static LocalWindow MorningHalfHour(LocalDate date) =>
        new(date, new LocalTime(MorningHour, 0), new LocalTime(MorningHour, HalfHour));

    private static Instant AtWarsaw(LocalDate date, int hour, int minute) =>
        date.At(new LocalTime(hour, minute)).InZoneStrictly(Warsaw).ToInstant();

    [Fact]
    public void Generate_SpringForward_SkipsTheLocalTimesThatDoNotExist()
    {
        var slots = Generate(Night(SpringForward));

        slots.Count.ShouldBe(SlotsOnTheSpringForwardNight);
        Generate(Night(OrdinaryNight)).Count.ShouldBe(SlotsOnAnOrdinaryNight);

        // 02:00 and 02:30 never happen, so the slot after 01:30 is 03:00 — not a shifted 02:00.
        slots[SlotsBeforeTheGap].ShouldBe(AtWarsaw(SpringForward, FirstHourAfterTheGap, 0));
    }

    [Fact]
    public void Generate_FallBack_ResolvesTheRepeatedLocalTimeToTheEarlierOffset()
    {
        var slots = Generate(Night(FallBack));

        // 02:00 local happens twice: once at UTC+2, an hour later at UTC+1. The earlier wins.
        slots.ShouldContain(Instant.FromUtc(
            FallBack.Year, FallBack.Month, FallBack.Day, RepeatedHour - CestOffsetHours, 0));
        slots.ShouldNotContain(Instant.FromUtc(
            FallBack.Year, FallBack.Month, FallBack.Day, RepeatedHour - CetOffsetHours, 0));
    }

    [Fact]
    public void Generate_EitherSideOfTheChange_MapsTheSameWallClockSlotToADifferentUtcHour()
    {
        var before = Generate(MorningHalfHour(BeforeFallBack));
        var after = Generate(MorningHalfHour(AfterFallBack));

        // Both are 09:00 on the organizer's wall clock. CEST is UTC+2, CET is UTC+1.
        before[0].ShouldBe(Instant.FromUtc(
            BeforeFallBack.Year, BeforeFallBack.Month, BeforeFallBack.Day,
            MorningHour - CestOffsetHours, 0));
        after[0].ShouldBe(Instant.FromUtc(
            AfterFallBack.Year, AfterFallBack.Month, AfterFallBack.Day,
            MorningHour - CetOffsetHours, 0));
    }
}
