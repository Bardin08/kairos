using Kairos.Api.Scheduling;

using NodaTime;

using Shouldly;

// NodaTime ships its own Interval; the alias says which one this file means.
using Interval = Kairos.Api.Scheduling.Interval;

namespace Kairos.Api.Tests.Scheduling;

/// <summary>
/// Duration is an input to slot generation, not a label: the same day is bookable for a short
/// call and unavailable for a long one, and that is the decision these tests hold in place.
/// </summary>
public class DurationTests
{
    private const int NoMargin = 0;
    private const int NoNotice = 0;
    private const int UnlimitedDailyCap = 99;

    private const int ShortCall = 25;
    private const int LongCall = 50;

    private const int WindowStartHour = 9;
    private const int WindowEndHour = 17;

    /// <summary>The one opening in an otherwise full day: 12:00 to 12:40.</summary>
    private const int GapStartHour = 12;
    private const int GapMinutes = 40;
    private const int OneSlotOffered = 1;

    private static readonly DateTimeZone Utc =
        DateTimeZoneProviders.Tzdb[SchedulingDefaults.TimeZoneId];
    private static readonly LocalDate Weekday = new(2026, 9, 7);
    private static readonly LocalWindow NineToFive = new(
        Weekday, new LocalTime(WindowStartHour, 0), new LocalTime(WindowEndHour, 0));
    private static readonly Dictionary<LocalDate, int> NoBookingsYet = [];

    private static readonly Limits NoLimits = new(
        NoMargin, NoNotice, UnlimitedDailyCap, SchedulingDefaults.StartIncrementMinutes);

    private static Instant At(int hour, int minute) =>
        Weekday.At(new LocalTime(hour, minute)).InZoneStrictly(Utc).ToInstant();

    /// <summary>The whole window booked except a forty-minute hole starting at 12:00.</summary>
    private static readonly BusyBlock[] DayWithAFortyMinuteGap =
    [
        new(new Interval(At(WindowStartHour, 0), At(GapStartHour, 0)), NoMargin),
        new(new Interval(At(GapStartHour, GapMinutes), At(WindowEndHour, 0)), NoMargin),
    ];

    private static IReadOnlyList<Instant> Generate(int durationMinutes) =>
        new SlotGenerator().Generate(
            [NineToFive],
            DayWithAFortyMinuteGap,
            Duration.FromMinutes(durationMinutes),
            NoLimits,
            At(0, 0),
            Utc,
            NoBookingsYet);

    [Fact]
    public void Generate_FortyMinuteGap_AcceptsTheShorterCall()
    {
        var slots = Generate(ShortCall);

        slots.Count.ShouldBe(OneSlotOffered);
        slots[0].ShouldBe(At(GapStartHour, 0));
    }

    [Fact]
    public void Generate_FortyMinuteGap_RefusesTheLongerCall() =>
        Generate(LongCall).ShouldBeEmpty();
}
