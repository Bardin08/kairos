using Kairos.Api.Scheduling;

using NodaTime;

using Shouldly;

// NodaTime ships its own Interval; the alias says which one this file means.
using Interval = Kairos.Api.Scheduling.Interval;

namespace Kairos.Api.Tests.Scheduling;

public class SlotGeneratorTests
{
    private const int NoMargin = 0;
    private const int NoNotice = 0;
    private const int UnlimitedDailyCap = 99;
    private const int HourLong = 60;
    private const int HalfHour = 30;

    /// <summary>The margin the busy meeting demands in the max-versus-sum test. Larger than the link's.</summary>
    private const int MeetingMargin = 30;

    private static readonly DateTimeZone Utc =
        DateTimeZoneProviders.Tzdb[SchedulingDefaults.TimeZoneId];
    private static readonly LocalDate Weekday = new(2026, 9, 7);
    private static readonly LocalWindow NineToFive =
        new(Weekday, new LocalTime(9, 0), new LocalTime(17, 0));
    private static readonly Dictionary<LocalDate, int> NoBookingsYet = [];

    private static Instant At(int hour, int minute) =>
        Weekday.At(new LocalTime(hour, minute)).InZoneStrictly(Utc).ToInstant();

    private static Limits Limits(
        int margin = NoMargin,
        int notice = NoNotice,
        int cap = UnlimitedDailyCap,
        int step = SchedulingDefaults.StartIncrementMinutes) =>
        new(margin, notice, cap, step);

    private static IReadOnlyList<Instant> Generate(
        BusyBlock[] busy,
        int durationMinutes,
        Limits limits,
        Instant now,
        IReadOnlyDictionary<LocalDate, int>? perDay = null) =>
        new SlotGenerator().Generate(
            [NineToFive],
            busy,
            Duration.FromMinutes(durationMinutes),
            limits,
            now,
            Utc,
            perDay ?? NoBookingsYet);

    [Fact]
    public void Generate_EmptyDay_ProducesSlotsOnTheIncrementInLocalTime()
    {
        var slots = Generate([], HourLong, Limits(), At(0, 0));

        slots[0].ShouldBe(At(9, 0));
        slots[1].ShouldBe(At(9, 30));
        slots[^1].ShouldBe(At(16, 0));
    }

    [Fact]
    public void Generate_SlotWouldRunPastTheWindow_IsNotOffered() =>
        Generate([], HourLong, Limits(), At(0, 0)).ShouldNotContain(At(16, 30));

    [Fact]
    public void Generate_MinimumNotice_RemovesSlotsThatAreTooSoon() =>
        Generate([], HourLong, Limits(notice: SchedulingDefaults.MinNoticeMinutes), At(9, 0))[0]
            .ShouldBe(At(11, 0));

    [Fact]
    public void Generate_BusyBlock_RemovesTheSlotsItCovers()
    {
        BusyBlock[] busy = [new(new Interval(At(12, 0), At(13, 0)), NoMargin)];

        var slots = Generate(busy, HourLong, Limits(), At(0, 0));

        slots.ShouldNotContain(At(12, 0));
        slots.ShouldNotContain(At(11, 30));
        slots.ShouldContain(At(11, 0));
        slots.ShouldContain(At(13, 0));
    }

    [Fact]
    public void Generate_TwoDifferentMargins_UsesTheLargerNotTheSum()
    {
        // Existing meeting demands 30, querying link demands 15. The gap is 30, not 45.
        BusyBlock[] busy = [new(new Interval(At(12, 0), At(13, 0)), MeetingMargin)];

        var slots = Generate(busy, HalfHour, Limits(margin: SchedulingDefaults.MarginMinutes), At(0, 0));

        slots.ShouldNotContain(At(13, 0));
        slots.ShouldContain(At(13, 30));
        slots.ShouldContain(At(11, 0));      // ends 11:30, touching the padded start
        slots.ShouldNotContain(At(11, 30));
    }

    [Fact]
    public void Generate_QueryingLinkHasTheLargerMargin_ItWins()
    {
        BusyBlock[] busy = [new(new Interval(At(12, 0), At(13, 0)), NoMargin)];

        var slots = Generate(busy, HalfHour, Limits(margin: HourLong), At(0, 0));

        slots.ShouldNotContain(At(13, 30));
        slots.ShouldContain(At(14, 0));
    }

    [Fact]
    public void Generate_DailyCapReached_ProducesNoSlotsForThatDay() =>
        Generate([], HourLong, Limits(cap: SchedulingDefaults.DailyCap), At(0, 0),
                 new Dictionary<LocalDate, int> { [Weekday] = SchedulingDefaults.DailyCap })
            .ShouldBeEmpty();
}
