using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;

using NodaTime;

namespace Kairos.Api.Integrations.Webhooks;

/// <summary>
/// The one event nothing can ask for: a meeting ending is the absence of a request, so a clock
/// has to notice it.
/// </summary>
/// <remarks>
/// It enqueues and never delivers. A service that noticed and sent would retry the noticing
/// whenever the sending failed, turning a send-side retry storm into a duplicate storm; delivery
/// is <see cref="OutboxDispatcher"/>'s, at its own pace.
///
/// The unique index on (BookingId, Type) is the guarantee, not the query below: the insert is
/// attempted, a duplicate rejected, the rejection swallowed. The query's last clause only stops
/// the service re-attempting every meeting it has ever announced — measured at 300 announced
/// bookings, a sweep issued 301 inserts of which 300 failed, every minute for the life of the
/// deployment.
///
/// That clause is not the check-then-act the design ruled out: one statement, filter and
/// selection evaluated together by SQLite, no window for another writer.
/// </remarks>
public sealed class MeetingEndedPoller(
    IServiceScopeFactory scopes,
    IClock clock,
    ILogger<MeetingEndedPoller> logger) : BackgroundService
{
    /// <summary>
    /// The interval the design fixed. A meeting ending is not urgent — nothing downstream is
    /// waiting on the second — and a minute is short enough that "it ended" and "we said so"
    /// read as the same moment to a human.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);

        // The first sweep runs at startup rather than a minute into it. A process that restarts
        // every minute would otherwise never sweep at all, and a restart is exactly when a
        // backlog of meetings that ended while it was down is waiting.
        while (await TickAsync(timer, stoppingToken))
        {
        }
    }

    /// <summary>Returns false when the host is shutting down.</summary>
    private async Task<bool> TickAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            await SweepAsync(stoppingToken);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            // A tick that throws must not end the service. The next minute's sweep sees the same
            // bookings — nothing was marked — so a transient database failure costs a minute of
            // latency rather than every meeting.ended from here on.
            logger.LogError(exception, "The meeting.ended sweep failed. Retrying on the next tick.");
        }

        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        // A scope per sweep, never a context shared with the dispatcher or the previous sweep:
        // DbContext is not thread-safe, and two services ticking through one is "a second
        // operation was started on this context" waiting for a busy minute.
        using var scope = scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<KairosDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<OutboxWriter>();

        var now = clock.GetCurrentInstant().ToUnixTimeSeconds();

        // Projected rather than loaded: the announcement needs four columns, and tracking whole
        // bookings would put every past meeting in the change tracker on every sweep.
        var ended = await context.Bookings
            .AsNoTracking()
            .Where(booking =>
                booking.Status == BookingStatus.Confirmed
                && booking.EndUtc <= now
                // The bound, and the reason a sweep costs what the last minute produced rather
                // than what the deployment has ever produced. It is a candidate filter, not the
                // correctness mechanism — see the class remarks.
                && !context.OutboxMessages.Any(message =>
                    message.BookingId == booking.Id
                    && message.Type == OutboxMessageType.MeetingEnded))
            .OrderBy(booking => booking.EndUtc)
            .Select(booking => new
            {
                booking.Id,
                booking.Link.Slug,
                booking.StartUtc,
                booking.EndUtc,
            })
            .ToListAsync(cancellationToken);

        foreach (var booking in ended)
        {
            await AnnounceAsync(context, outbox, booking.Id, BookingEventPayload.Ended(
                booking.Id,
                booking.Slug,
                Instant.FromUnixTimeSeconds(booking.StartUtc),
                Instant.FromUnixTimeSeconds(booking.EndUtc)).ToJson(), cancellationToken);
        }
    }

    /// <summary>
    /// One SaveChanges per row, not per sweep: a single duplicate rolls a whole batch back, and
    /// the steady state here is a sweep in which every row is a duplicate.
    /// </summary>
    private async Task AnnounceAsync(
        KairosDbContext context,
        OutboxWriter outbox,
        Guid bookingId,
        string payload,
        CancellationToken cancellationToken)
    {
        var message = outbox.Enqueue(bookingId, OutboxMessageType.MeetingEnded, payload);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Queued meeting.ended for booking {Booking}.", bookingId);
        }
        catch (DbUpdateException exception) when (SqliteErrors.IsUniqueViolation(exception))
        {
            // Already announced, and the index said so. Detached rather than left Added: a
            // rejected insert stays in the change tracker, and the next booking's SaveChanges
            // would carry it along and fail on the same constraint again.
            context.Entry(message).State = EntityState.Detached;
        }
    }
}
