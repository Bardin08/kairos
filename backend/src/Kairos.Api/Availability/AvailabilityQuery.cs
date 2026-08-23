using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Scheduling;
using Kairos.Api.Shared;

using Microsoft.EntityFrameworkCore;

using NodaTime;

namespace Kairos.Api.Availability;

/// <summary>
/// Stored availability turned into wall-clock windows: the weekly rules expanded across a date
/// range, with a date that has an override replaced rather than added to. Two round trips, both
/// compiled, because this pair runs on every load of every booking page.
/// </summary>
public sealed class AvailabilityQuery(KairosDbContext context)
{
    /// <summary>
    /// A stored window that cannot exist. Reported rather than expanded: the schema has no CHECK
    /// on these columns, and a window whose end is not after its start behaves in ways that only
    /// show up three layers down, inside the slot walk.
    /// </summary>
    public const string CorruptPatternCode = "pattern_invalid";

    private const int MinutesPerHour = 60;

    /// <summary>
    /// 23:59. A window may not end at 1440: <see cref="LocalWindow"/> holds a
    /// <see cref="LocalTime"/>, which has no 24:00, so midnight would silently become 00:00 and
    /// erase the day. "Until the end of the day" is 1439.
    /// </summary>
    /// <remarks>
    /// Public because the admin write side declares the same bound: a window this class refuses to
    /// expand must be one the operator was never able to save, and two copies of 1439 is one of
    /// them going stale.
    /// </remarks>
    public const int LastMinuteOfDay = (24 * MinutesPerHour) - 1;

    private static readonly Func<KairosDbContext, Guid, IAsyncEnumerable<PatternRule>> RulesOfPattern =
        EF.CompileAsyncQuery((KairosDbContext database, Guid patternId) =>
            database.PatternRules.AsNoTracking()
                .Where(rule => rule.PatternId == patternId));

    private static readonly Func<KairosDbContext, Guid, DateOnly, DateOnly, IAsyncEnumerable<PatternOverride>> OverridesOfPattern =
        EF.CompileAsyncQuery((KairosDbContext database, Guid patternId, DateOnly from, DateOnly to) =>
            database.PatternOverrides.AsNoTracking()
                .Where(replacement => replacement.PatternId == patternId
                    && replacement.Date >= from
                    && replacement.Date <= to));

    /// <summary>The range is inclusive of both ends.</summary>
    public async Task<LocalWindow[]> GetWindowsAsync(
        Guid patternId,
        LocalDate from,
        LocalDate to,
        CancellationToken cancellationToken)
    {
        var rules = new List<PatternRule>();
        await foreach (var rule in RulesOfPattern(context, patternId).WithCancellation(cancellationToken))
        {
            rules.Add(rule);
        }

        var replacements = new Dictionary<DateOnly, List<PatternOverride>>();
        await foreach (var replacement in OverridesOfPattern(context, patternId, from.ToDateOnly(), to.ToDateOnly())
                           .WithCancellation(cancellationToken))
        {
            if (!replacements.TryGetValue(replacement.Date, out var forDate))
            {
                forDate = [];
                replacements[replacement.Date] = forDate;
            }

            forDate.Add(replacement);
        }

        // Once per date rather than per day of the walk, and for the reason the rules below are
        // sorted: a date split into a morning and an afternoon comes back mornings first.
        foreach (var forDate in replacements.Values)
        {
            forDate.Sort((left, right) => Nullable.Compare(left.StartMinute, right.StartMinute));
        }

        // Sorted here rather than in SQL so the compiled query keeps returning IQueryable, and
        // because a split day should come back mornings first whatever order SQLite chose.
        var byIsoDay = rules.OrderBy(rule => rule.StartMinute).ToLookup(rule => rule.IsoDayOfWeek);
        var windows = new List<LocalWindow>();

        for (var date = from; date <= to; date = date.PlusDays(1))
        {
            if (replacements.TryGetValue(date.ToDateOnly(), out var forDate))
            {
                // An override IS the day: the weekly rules are not merged in, and a date whose
                // only row has no times leaves the day empty rather than falling back to them.
                foreach (var replacement in forDate)
                {
                    if (replacement.StartMinute is { } overrideStart && replacement.EndMinute is { } overrideEnd)
                    {
                        windows.Add(Window(patternId, date, overrideStart, overrideEnd));
                    }
                }

                continue;
            }

            // ILookup rather than an array indexed by day: IsoDayOfWeek has no CHECK either, and
            // a miss here is an empty sequence instead of an IndexOutOfRangeException.
            foreach (var rule in byIsoDay[(int)date.DayOfWeek])
            {
                windows.Add(Window(patternId, date, rule.StartMinute, rule.EndMinute));
            }
        }

        return [.. windows];
    }

    private static LocalWindow Window(Guid patternId, LocalDate date, int startMinute, int endMinute)
    {
        if (startMinute < 0 || endMinute > LastMinuteOfDay || endMinute <= startMinute)
        {
            throw DomainException.Invalid(
                CorruptPatternCode,
                $"Pattern {patternId} puts {startMinute}-{endMinute} on {date}, outside 0-{LastMinuteOfDay} or ending before it starts.");
        }

        return new LocalWindow(date, ToLocalTime(startMinute), ToLocalTime(endMinute));
    }

    private static LocalTime ToLocalTime(int minuteOfDay) =>
        new(minuteOfDay / MinutesPerHour, minuteOfDay % MinutesPerHour);
}
