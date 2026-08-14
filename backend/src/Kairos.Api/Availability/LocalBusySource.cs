using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Scheduling;

using Microsoft.EntityFrameworkCore;

using NodaTime;

namespace Kairos.Api.Availability;

/// <summary>
/// The organizer's own confirmed bookings. Every link's bookings, not just the queried one:
/// availability is one calendar, and a link that could only see its own would happily sell an
/// hour another link already sold.
/// </summary>
public sealed class LocalBusySource(KairosDbContext context) : IBusySource
{
    private const int SecondsPerMinute = 60;

    /// <summary>
    /// The margin is resolved per row — COALESCE(link, settings) — because it belongs to the
    /// meeting that is already in the calendar, not to whoever is asking. The row's own margin
    /// also widens the overlap test, so a meeting that ends before the window still comes back
    /// when its margin reaches into it.
    /// </summary>
    public async Task<IReadOnlyList<BusyBlock>> GetBusyAsync(
        Scheduling.Interval window,
        CancellationToken cancellationToken)
    {
        // Not named from/to: `from` is a query keyword and this method is written in query syntax.
        var windowStart = window.Start.ToUnixTimeSeconds();
        var windowEnd = window.End.ToUnixTimeSeconds();

        var rows = await (
            from booking in context.Bookings.AsNoTracking()
            join link in context.Links on booking.LinkId equals link.Id
            from settings in context.Settings
            where settings.Id == Settings.SingletonId
                && booking.Status == BookingStatus.Confirmed
                && (booking.StartUtc - ((link.MarginMinutes ?? settings.MarginMinutes) * SecondsPerMinute)) < windowEnd
                && (booking.EndUtc + ((link.MarginMinutes ?? settings.MarginMinutes) * SecondsPerMinute)) > windowStart
            select new
            {
                booking.StartUtc,
                booking.EndUtc,
                MarginMinutes = link.MarginMinutes ?? settings.MarginMinutes,
            })
            .ToListAsync(cancellationToken);

        var busy = new List<BusyBlock>(rows.Count);
        foreach (var row in rows)
        {
            busy.Add(new BusyBlock(
                new Scheduling.Interval(
                    Instant.FromUnixTimeSeconds(row.StartUtc),
                    Instant.FromUnixTimeSeconds(row.EndUtc)),
                row.MarginMinutes));
        }

        return busy;
    }
}
