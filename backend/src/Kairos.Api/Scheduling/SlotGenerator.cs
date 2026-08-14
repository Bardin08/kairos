using NodaTime;

namespace Kairos.Api.Scheduling;

/// <summary>
/// The hot path: runs on every booking-page load, for every duration, across a date range.
/// Pure — no I/O, no clock, no database — so DST, margin and duration behaviour are
/// exhaustively testable, and allocation-aware because a month of slots is a lot of garbage
/// to make in order to answer one question.
/// </summary>
public sealed class SlotGenerator
{
    private const int MinutesPerHour = 60;
    private const int MinutesPerDay = 1440;

    public IReadOnlyList<Instant> Generate(
        ReadOnlySpan<LocalWindow> windows,
        ReadOnlySpan<BusyBlock> busy,
        Duration slotDuration,
        Limits limits,
        Instant now,
        DateTimeZone zone,
        IReadOnlyDictionary<LocalDate, int> bookingsPerDay)
    {
        // Pad each busy interval by max(its margin, the querying link's margin), and leave the
        // candidate UNPADDED. Padding both would demand the SUM of the two margins.
        var blocked = new Interval[busy.Length];
        for (var index = 0; index < busy.Length; index++)
        {
            var padding = Duration.FromMinutes(
                Math.Max(busy[index].MarginMinutes, limits.MarginMinutes));
            blocked[index] = busy[index].Interval.Pad(padding);
        }

        var earliest = now + Duration.FromMinutes(limits.MinNoticeMinutes);
        var step = limits.StartIncrementMinutes;
        var durationMinutes = (int)slotDuration.TotalMinutes;

        var slots = new List<Instant>(windows.Length * (MinutesPerDay / Math.Max(step, 1)));

        foreach (var window in windows)
        {
            if (bookingsPerDay.TryGetValue(window.Date, out var taken) && taken >= limits.DailyCap)
            {
                continue;
            }

            for (var local = AlignUp(window.Start, step);
                 local < window.End;
                 local = local.PlusMinutes(step))
            {
                // Compared in local wall time deliberately: "my window is 09:00-17:00" is a
                // statement about the clock on the wall, not about elapsed UTC.
                if (local.PlusMinutes(durationMinutes) > window.End)
                {
                    break;
                }

                if (!TryMap(window.Date.At(local), zone, out var start) || start < earliest)
                {
                    continue;
                }

                var slot = new Interval(start, start + slotDuration);
                if (!Collides(blocked, slot))
                {
                    slots.Add(start);
                }
            }
        }

        slots.Sort();
        return slots;
    }

    /// <remarks>A plain loop, not <c>Any</c>: this runs once per candidate slot per request.</remarks>
    private static bool Collides(Interval[] blocked, in Interval slot)
    {
        for (var index = 0; index < blocked.Length; index++)
        {
            if (blocked[index].Overlaps(slot))
            {
                return true;
            }
        }

        return false;
    }

    private static LocalTime AlignUp(LocalTime time, int stepMinutes)
    {
        var minutes = (time.Hour * MinutesPerHour) + time.Minute;
        var remainder = minutes % stepMinutes;
        if (remainder == 0 && time.Second == 0 && time.NanosecondOfSecond == 0)
        {
            return time;
        }

        return new LocalTime(0, 0).PlusMinutes(minutes - remainder + stepMinutes);
    }

    /// <summary>
    /// A local time that does not exist (spring forward) is skipped; one that happens twice
    /// (fall back) resolves to the first, earlier occurrence.
    /// </summary>
    private static bool TryMap(LocalDateTime local, DateTimeZone zone, out Instant instant)
    {
        var mapping = zone.MapLocal(local);
        if (mapping.Count == 0)
        {
            instant = default;
            return false;
        }

        instant = mapping.First().ToInstant();
        return true;
    }
}
