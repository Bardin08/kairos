using System.Runtime.InteropServices;

using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Scheduling;
using Kairos.Api.Shared;

using Microsoft.EntityFrameworkCore;

using NodaTime;

namespace Kairos.Api.Availability;

/// <summary>
/// The whole read side of the booking page in one call: resolve the link, resolve its limits
/// against the globals, expand the pattern, drain every busy source, count the day's bookings,
/// generate. It throws instead of returning a result type — the endpoint above it has no failure
/// branch to write, and the middleware turns each of these into ProblemDetails.
/// </summary>
public sealed class SlotQuery(
    KairosDbContext context,
    AvailabilityQuery availability,
    SlotGenerator generator,
    IEnumerable<IBusySource> busySources)
{
    /// <summary>
    /// Inclusive days one query may span. A public endpoint with no upper bound is an invitation
    /// to ask a Raspberry Pi to expand a decade; 31 covers "show me next month" and stops.
    /// It bounds the request, not the generator's allocation — that also rises with the length of
    /// the availability day and falls with the start increment, and this caps neither.
    /// </summary>
    public const int MaxRangeDays = 31;

    public const string RangeTooWideCode = "range_too_wide";

    public const string DurationRequiredCode = "duration_required";

    public const string DurationNotOfferedCode = "duration_not_offered";

    public const string NoDurationsCode = "link_has_no_durations";

    /// <summary>
    /// <paramref name="now"/> is an argument rather than a clock read inside, because minimum
    /// notice is measured from it and the create-booking path has to re-check against the same
    /// instant this page offered from. A clock in here would let read and write disagree at the
    /// boundary, and the symptom is a slot the page offers being rejected on submit.
    /// </summary>
    public async Task<SlotResults> GetSlotsAsync(
        string slug,
        int? durationMinutes,
        LocalDate from,
        LocalDate to,
        Instant now,
        CancellationToken cancellationToken)
    {
        var days = Period.DaysBetween(from, to) + 1;
        if (days > MaxRangeDays)
        {
            throw DomainException.Invalid(
                RangeTooWideCode, $"A range of {days} days is wider than the {MaxRangeDays} this answers for.");
        }

        var link = await context.Links.AsNoTracking()
            .Include(candidate => candidate.Durations)
            .FirstOrDefaultAsync(candidate => candidate.Slug == slug && candidate.Active, cancellationToken)
            // Not "Link '{slug}'": the caller's value is never reflected into a response body, the
            // rule LinksEndpoints states and SlotsEndpoints.ParseDate keeps. It also made one
            // condition answer two different sentences depending on which of the two link routes
            // an anonymous caller happened to send an unknown slug to.
            ?? throw DomainException.NotFound("Link");

        // SortOrder, not ascending length: it is the order the operator chose for the booking
        // page's tabs, it comes back out in SlotResults, and it is what decides which duration a
        // request with no duration on it gets.
        var offered = link.Durations
            .OrderBy(duration => duration.SortOrder)
            .Select(duration => duration.Minutes)
            .ToList();
        var minutes = Resolve(offered, durationMinutes);

        var settings = await context.Settings.AsNoTracking()
            .SingleAsync(row => row.Id == Settings.SingletonId, cancellationToken);

        var limits = new Limits(
                settings.MarginMinutes,
                settings.MinNoticeMinutes,
                settings.DailyCap,
                settings.StartIncrementMinutes)
            .With(new LimitOverrides(
                link.MarginMinutes, link.MinNoticeMinutes, link.DailyCap, link.StartIncrementMinutes));

        var zone = DateTimeZoneProviders.Tzdb[settings.TimeZoneId];
        var windows = await availability.GetWindowsAsync(link.PatternId, from, to, cancellationToken);

        // Leniently, because a range can start on the day a zone springs forward and midnight
        // itself is skipped in a handful of zones. Ambiguous resolves early, skipped resolves to
        // the moment the gap ends — either way the range covers the whole local day.
        var rangeStart = from.AtMidnight().InZoneLeniently(zone).ToInstant();
        var rangeEnd = to.PlusDays(1).AtMidnight().InZoneLeniently(zone).ToInstant();

        var busy = await GatherBusyAsync(rangeStart, rangeEnd, limits.MarginMinutes, cancellationToken);
        var bookingsPerDay = await CountPerLocalDayAsync(rangeStart, rangeEnd, from, to, zone, cancellationToken);

        var starts = generator.Generate(
            windows,
            CollectionsMarshal.AsSpan(busy),
            Duration.FromMinutes(minutes),
            limits,
            now,
            zone,
            bookingsPerDay);

        return new SlotResults(link.Id, minutes, offered, limits, starts);
    }

    /// <summary>
    /// The one duration this query is for. A link with no durations at all is reachable — the
    /// rule spans two tables and SQLite has no deferred CHECK — and <c>Durations.Default</c> is
    /// <c>durations[0]</c>, so without this the public page dies on an
    /// <see cref="ArgumentOutOfRangeException"/> that names "index" and nothing else.
    /// </summary>
    /// <remarks>
    /// No slug reaches these three messages, and the query no longer takes one to build them with.
    /// The rule is <see cref="Features.Links.LinksEndpoints"/>'s: the caller's value is never
    /// reflected into a response body, and it costs the sentence nothing here — the slug is in the
    /// URL the caller just sent.
    /// </remarks>
    private static int Resolve(IReadOnlyList<int> offered, int? requested)
    {
        if (offered.Count == 0)
        {
            throw DomainException.Invalid(NoDurationsCode, "That link offers no durations.");
        }

        if (requested is not { } minutes)
        {
            if (!offered.IsSingle)
            {
                throw DomainException.Invalid(
                    DurationRequiredCode, $"That link offers {offered.Count} durations; pick one.");
            }

            return offered.Default;
        }

        if (!offered.Offers(minutes))
        {
            throw DomainException.Invalid(
                DurationNotOfferedCode, $"That link does not offer {minutes} minutes.");
        }

        return minutes;
    }

    /// <summary>
    /// Widened by the querying link's margin so a meeting just outside the range still blocks the
    /// slot next to it; each source widens further by its own blocks' margins. Nothing is padded
    /// here — <see cref="SlotGenerator"/> applies the larger of the two, and padding twice would
    /// demand their sum.
    /// </summary>
    private async Task<List<BusyBlock>> GatherBusyAsync(
        Instant rangeStart,
        Instant rangeEnd,
        int marginMinutes,
        CancellationToken cancellationToken)
    {
        var margin = Duration.FromMinutes(marginMinutes);
        var window = new Scheduling.Interval(rangeStart - margin, rangeEnd + margin);

        var busy = new List<BusyBlock>();
        foreach (var source in busySources)
        {
            busy.AddRange(await source.GetBusyAsync(window, cancellationToken));
        }

        return busy;
    }

    /// <summary>
    /// Confirmed bookings per day, keyed on the ORGANIZER's local date. Grouping on the UTC date
    /// of StartUtc is the obvious version and is wrong for every organizer not on UTC: the cap is
    /// stated in their days, not Greenwich's.
    /// </summary>
    private async Task<Dictionary<LocalDate, int>> CountPerLocalDayAsync(
        Instant rangeStart,
        Instant rangeEnd,
        LocalDate from,
        LocalDate to,
        DateTimeZone zone,
        CancellationToken cancellationToken)
    {
        var startSeconds = rangeStart.ToUnixTimeSeconds();
        var endSeconds = rangeEnd.ToUnixTimeSeconds();

        var starts = await context.Bookings.AsNoTracking()
            .Where(booking => booking.Status == BookingStatus.Confirmed
                && booking.StartUtc >= startSeconds
                && booking.StartUtc < endSeconds)
            .Select(booking => booking.StartUtc)
            .ToListAsync(cancellationToken);

        // Every day in range gets a key, including the empty ones. SlotGenerator compares the cap
        // only where a key exists, so a sparse dictionary would let DailyCap = 0 offer a full day.
        var perDay = new Dictionary<LocalDate, int>();
        for (var date = from; date <= to; date = date.PlusDays(1))
        {
            perDay[date] = 0;
        }

        foreach (var start in starts)
        {
            var date = Instant.FromUnixTimeSeconds(start).InZone(zone).Date;
            if (perDay.TryGetValue(date, out var counted))
            {
                perDay[date] = counted + 1;
            }
        }

        return perDay;
    }
}
