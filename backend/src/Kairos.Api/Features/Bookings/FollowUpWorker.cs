using Kairos.Api.Integrations;
using Kairos.Api.Integrations.Email;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Security;

using Microsoft.EntityFrameworkCore;

using NodaTime;

namespace Kairos.Api.Features.Bookings;

/// <summary>
/// Everything a committed booking owes a third party, done off the request thread.
/// </summary>
/// <remarks>
/// Shaped after <see cref="Integrations.Webhooks.OutboxDispatcher"/> — a scope per pass,
/// oldest-first, a failure recorded on the row and retried later, and nothing a provider does may
/// take the service down — and it differs in two ways that are both deliberate.
///
/// It wakes on a nudge, so a booker's mail leaves in about a second rather than in up to ten; the
/// timer stays underneath as the floor, which is what makes the nudge droppable.
///
/// And a finished row is deleted rather than marked. It carries a manage token the database
/// otherwise holds only a hash of, so the row must not outlive the send. An exhausted row is kept
/// — an operator has to be able to find out why a booker was never told — but its secret is nulled
/// on the way out, and the attempt cap is what removes it from the queue.
/// </remarks>
public sealed class FollowUpWorker(
    IServiceScopeFactory scopes,
    FollowUpSignal signal,
    ILogger<FollowUpWorker> logger) : BackgroundService
{
    /// <summary>
    /// The floor under the nudge, not the schedule. Ten seconds is what a lost nudge costs, and a
    /// pass that finds nothing costs one indexed query.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many follow-ups one pass will attempt. Bounded so a backlog — a relay down for a day —
    /// drains over successive passes rather than holding one scope open for hours.
    /// </summary>
    private const int BatchSize = 100;

    /// <summary>Public because the tests drive a row to exhaustion and have to know when that is.</summary>
    public const int MaxAttempts = 5;

    /// <summary>Enough of a failure to act on, in a database meant to stay small.</summary>
    private const int MaxErrorLength = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first pass waits, the way ProposalSweeper's does: nothing queued before this
        // process started is urgent, because it has already waited minutes or hours for a
        // process to come back — ten more seconds costs nothing anybody can perceive. Live
        // traffic never depends on this at all; the nudge covers every row enqueued while the
        // process is up. Reading at boot at the same instant as another hosted service is a cost
        // with no offsetting benefit, which is what waiting first removes.
        while (!stoppingToken.IsCancellationRequested)
        {
            await WaitAsync(stoppingToken);

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                // A pass that threw must not take the service down: a worker that is gone is a
                // silent drop of every booking after it.
                logger.LogError(exception, "A follow-up pass failed. The next one will try again.");
            }
        }
    }

    /// <summary>
    /// One pass. Public so a test can drive it without a timer: the loop above is the schedule.
    /// </summary>
    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        // A scope per pass, never one shared across passes: DbContext is scoped, is not
        // thread-safe, and this ticks independently of every request and of the services beside it.
        using var scope = scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<KairosDbContext>();

        // Exhausted rows leave the queue rather than being skipped in it, or every pass would
        // re-read every dead row for ever. They keep their Attempts and LastError, which is where
        // an operator reads what happened.
        var pending = await context.FollowUps
            .Where(followUp => followUp.Attempts < MaxAttempts)
            .OrderBy(followUp => followUp.CreatedAt)
            .ThenBy(followUp => followUp.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var followUp in pending)
        {
            await RunAsync(scope.ServiceProvider, context, followUp, cancellationToken);
        }
    }

    /// <summary>
    /// Waits for a nudge or for the interval, whichever comes first. Task.Delay rather than
    /// PeriodicTimer: a timer permits only one outstanding WaitForNextTickAsync, so it cannot be
    /// raced against anything.
    /// </summary>
    private async Task WaitAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var floor = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var nudged = signal.WaitAsync(floor.Token);
            var elapsed = Task.Delay(TickInterval, floor.Token);

            await Task.WhenAny(nudged, elapsed);

            // Whichever lost is cancelled rather than left running, or every pass leaks a timer or
            // a channel read that outlives the pass that started it.
            await floor.CancelAsync();
        }
        catch (OperationCanceledException)
        {
            // Shutdown, or the loser of the race being cancelled. Neither is worth a line.
        }
    }

    /// <summary>
    /// The steps for one row, in the order the inline code ran them: the calendar first, so the
    /// notice describes a calendar the organizer's side already agrees with, and then the notice.
    /// </summary>
    /// <remarks>
    /// The booking is read fresh rather than carried on the row, so a handler acts on what is true
    /// now. A booking rescheduled twice inside one pass therefore has its first notice describe the
    /// second time — the invitee is told the truth, and the alternative is a second copy of the
    /// booking's own state machine.
    ///
    /// <see cref="CalendarSyncService"/> never throws — every path out of it is a logged return —
    /// so nothing here retries a calendar step on its own account; what drives a retry is the
    /// notice, through <see cref="RequireSentAsync"/>.
    /// </remarks>
    private async Task RunAsync(
        IServiceProvider services,
        KairosDbContext context,
        FollowUp followUp,
        CancellationToken cancellationToken)
    {
        try
        {
            var calendars = services.GetRequiredService<CalendarSyncService>();
            var identities = services.GetRequiredService<MeetingIdentityResolver>();
            var mailer = services.GetRequiredService<BookingMailer>();
            var protector = services.GetRequiredService<SecretProtector>();

            var booking = await context.Bookings
                .AsNoTracking()
                .Include(row => row.Link)
                .Include(row => row.Participants)
                .SingleAsync(row => row.Id == followUp.BookingId, cancellationToken);

            var manageToken = followUp.Secret is null ? null : protector.Unprotect(followUp.Secret);

            switch (followUp.Type)
            {
                case FollowUpType.Created:
                    {
                        // A Created row is always written with a secret — FollowUpWriter.Enqueue is
                        // its only caller and always hands one — so a null here is a queue corrupted
                        // rather than a mail with nothing to link, and is worth its own message rather
                        // than a NullReferenceException three calls deep in the mailer.
                        if (manageToken is null)
                        {
                            throw new InvalidOperationException(
                                $"Follow-up {followUp.Id} is Created but carries no secret to confirm with.");
                        }

                        await calendars.SyncAsync(booking, cancellationToken);
                        var joinUrl = await JoinUrlAsync(context, booking.Id, cancellationToken);
                        var identity = await identities.ResolveAsync(booking.Id, cancellationToken);
                        await RequireSentAsync(followUp, mailer.SendConfirmationAsync(
                            booking, identity, manageToken, joinUrl, cancellationToken));
                        break;
                    }

                case FollowUpType.Rescheduled:
                    {
                        await calendars.RescheduleAsync(booking, cancellationToken);
                        var joinUrl = await JoinUrlAsync(context, booking.Id, cancellationToken);
                        var identity = await identities.ResolveAsync(booking.Id, cancellationToken);
                        await RequireSentAsync(followUp, mailer.SendRescheduleAsync(
                            booking,
                            identity,
                            manageToken,
                            joinUrl,
                            followUp.PreviousStartUtc is { } previous
                                ? Instant.FromUnixTimeSeconds(previous)
                                : null,
                            cancellationToken));
                        break;
                    }

                case FollowUpType.Cancelled:
                    {
                        await calendars.CancelAsync(booking.Id, cancellationToken);
                        var identity = await identities.ResolveAsync(booking.Id, cancellationToken);
                        await RequireSentAsync(followUp, mailer.SendCancellationAsync(
                            booking, identity, manageToken, cancellationToken));
                        break;
                    }

                case FollowUpType.Updated:
                    {
                        // A REQUEST would put a meeting cancelled since the resync back on the
                        // invitee's calendar. A moved one is Confirmed, read here at its new time.
                        if (booking.Status != BookingStatus.Confirmed)
                        {
                            break;
                        }

                        var joinUrl = await JoinUrlAsync(context, booking.Id, cancellationToken);
                        var identity = await identities.ResolveAsync(booking.Id, cancellationToken);
                        await RequireSentAsync(followUp, mailer.SendUpdateAsync(
                            booking, identity, joinUrl, cancellationToken));
                        break;
                    }

                default:
                    throw new InvalidOperationException(
                        $"Follow-up {followUp.Id} has no handler for {followUp.Type}.");
            }

            // Only here, and a delete rather than a mark: the secret must not outlive its use.
            context.FollowUps.Remove(followUp);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            followUp.Attempts++;
            followUp.LastError = Truncate(exception.Message);

            if (followUp.Attempts >= MaxAttempts)
            {
                // The row stays, so an operator has something to find; the secret goes, because
                // nothing will ever spend it now.
                followUp.Secret = null;

                logger.LogError(
                    exception,
                    "Giving up on follow-up {FollowUp} ({Type}) for booking {Booking} after "
                    + "{Attempts} attempts. Nobody will be told about this booking.",
                    followUp.Id,
                    followUp.Type,
                    followUp.BookingId,
                    followUp.Attempts);
            }
            else
            {
                logger.LogWarning(
                    exception,
                    "Follow-up {FollowUp} failed (attempt {Attempts}). Retrying on a later pass.",
                    followUp.Id,
                    followUp.Attempts);
            }
        }

        // Uncancellable, for OutboxDispatcher's reason: this records work a third party has already
        // done or refused, and a shutdown between the send and the mark is what causes a repeat.
        await context.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// Turns a swallowed refusal back into a failure the catch above can act on.
    /// </summary>
    /// <remarks>
    /// <see cref="BookingMailer"/>'s three notice methods answer rule 14 the way
    /// <see cref="OperatorMailer.SendAsync"/> already does for the operator's own mail: they never
    /// throw for a relay that refused, they report it. A caller with nowhere to retry — the inline
    /// endpoints, today — is right to let that answer disappear; this caller exists to retry, so it
    /// asks the question back.
    /// </remarks>
    private static async Task RequireSentAsync(FollowUp followUp, Task<bool> sending)
    {
        if (!await sending)
        {
            throw new InvalidOperationException(
                $"The relay would not take the notice for follow-up {followUp.Id}.");
        }
    }

    /// <summary>
    /// Where this meeting is joined, or null when it has no conference — an ordinary state, so the
    /// notice goes without it. A correlated read because Booking has no navigation to its
    /// conference links, and giving it one so a mail could use it would be a schema change made by
    /// a message.
    /// </summary>
    private static Task<string?> JoinUrlAsync(
        KairosDbContext context,
        Guid bookingId,
        CancellationToken cancellationToken) =>
        context.ConferenceLinks.AsNoTracking()
            .Where(row => row.BookingId == bookingId)
            .Select(row => (string?)row.JoinUrl)
            .FirstOrDefaultAsync(cancellationToken);

    private static string Truncate(string message) =>
        message.Length <= MaxErrorLength ? message : message[..MaxErrorLength];
}
