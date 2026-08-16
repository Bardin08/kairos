using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;

using NodaTime;

namespace Kairos.Api.Integrations.Webhooks;

/// <summary>
/// The one event nothing can ask for: a meeting ending is the absence of a request, so a clock
/// has to notice it. Every minute this asks which confirmed bookings are now over and enqueues
/// <c>meeting.ended</c> for each.
/// </summary>
/// <remarks>
/// It enqueues and it never delivers. Those are separate jobs on purpose — a service that
/// noticed and sent would retry the noticing whenever the sending failed, and a retry storm on
/// the send side would become a duplicate storm on the notice side. Delivery is
/// <see cref="OutboxDispatcher"/>'s, from the queue, at its own pace.
///
/// The unique index on (BookingId, Type) is what makes announcing twice impossible, not the
/// query below. The insert is attempted, a duplicate is rejected, the rejection is swallowed —
/// that backstop is the guarantee and nothing here replaces it. What the query's last clause
/// does is stop the service from re-attempting every meeting it has ever announced on every
/// tick: measured at 300 already-announced bookings, a sweep issued 301 inserts of which 300
/// failed, and that is per minute for the life of the deployment.
///
/// The clause is not the check-then-act the design ruled out. That is read, decide, then write
/// with a window in between for another writer; this is one statement, filter and selection
/// evaluated together by SQLite, and no window at all. If a duplicate somehow reaches the insert
/// anyway — a second process, a sweep overlapping itself — the index still refuses it and the
/// swallow below still handles it.
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

    /// <summary>
    /// One sweep, then the wait. Returns false when the host is shutting down.
    /// </summary>
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
        // A scope per sweep, and never a context shared with the dispatcher or with the previous
        // sweep: DbContext is not thread-safe, and two services ticking on their own schedules
        // through one context is "a second operation was started on this context" waiting for a
        // busy minute to happen on.
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
    /// One booking, one SaveChanges. Per row rather than per sweep because a single duplicate in
    /// a batch would roll the whole batch back, and the steady state of this service is a sweep
    /// in which every row is a duplicate — so the batch that failed would be all of them, every
    /// minute, forever.
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
