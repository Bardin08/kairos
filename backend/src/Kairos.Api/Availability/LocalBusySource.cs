using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Scheduling;

using Microsoft.EntityFrameworkCore;

using NodaTime;

namespace Kairos.Api.Availability;

/// <summary>
/// The organizer's own confirmed bookings, and the slots held by open proposals. Every link's,
/// not just the queried one: availability is one calendar, and a link that could only see its own
/// would happily sell an hour another link already sold.
/// </summary>
/// <remarks>
/// A hold is busy while its proposal is open, its expiry is in the future and the meeting behind
/// it is still on. The expiry is compared here rather than trusted to the sweeper that marks
/// lapsed rows, and the status is read rather than assumed closed with the booking: a hold whose
/// expiry has passed or whose meeting was cancelled underneath it stops holding at that instant,
/// so a sweeper that is late, dead or never started makes the mail late and never makes
/// availability wrong.
/// </remarks>
public sealed class LocalBusySource(KairosDbContext context, IClock clock) : IBusySource
{
    private const int SecondsPerMinute = 60;

    /// <summary>
    /// The margin is resolved per row — COALESCE(link, settings) — because it belongs to the
    /// meeting that is already in the calendar, not to whoever is asking. The row's own margin
    /// also widens the overlap test, so a meeting that ends before the window still comes back
    /// when its margin reaches into it. A held slot is padded by the margin of the booking it
    /// belongs to, because that is the meeting it will become.
    /// </summary>
    public async Task<IReadOnlyList<BusyBlock>> GetBusyAsync(
        Scheduling.Interval window,
        CancellationToken cancellationToken)
    {
        // Not named from/to: `from` is a query keyword and this method is written in query syntax.
        var windowStart = window.Start.ToUnixTimeSeconds();
        var windowEnd = window.End.ToUnixTimeSeconds();
        var now = clock.GetCurrentInstant().ToUnixTimeSeconds();

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

        var held = await (
            from proposal in context.BookingProposals.AsNoTracking()
            join booking in context.Bookings on proposal.BookingId equals booking.Id
            join link in context.Links on booking.LinkId equals link.Id
            from settings in context.Settings
            where settings.Id == Settings.SingletonId
                && proposal.State == ProposalState.Open
                && booking.Status == BookingStatus.Confirmed
                && proposal.ExpiresAt > now
                && (proposal.StartUtc - ((link.MarginMinutes ?? settings.MarginMinutes) * SecondsPerMinute)) < windowEnd
                && (proposal.EndUtc + ((link.MarginMinutes ?? settings.MarginMinutes) * SecondsPerMinute)) > windowStart
            select new
            {
                proposal.StartUtc,
                proposal.EndUtc,
                MarginMinutes = link.MarginMinutes ?? settings.MarginMinutes,
            })
            .ToListAsync(cancellationToken);

        var busy = new List<BusyBlock>(rows.Count + held.Count);
        foreach (var row in rows.Concat(held))
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
