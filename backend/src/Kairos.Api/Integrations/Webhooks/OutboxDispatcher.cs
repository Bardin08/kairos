using System.Globalization;
using System.Net.Mime;
using System.Text;

using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using NodaTime;

namespace Kairos.Api.Integrations.Webhooks;

/// <summary>
/// The other half of the outbox: everything queued and not yet delivered, POSTed oldest-first
/// and signed.
/// </summary>
/// <remarks>
/// The mark comes after the send, never before. A crash in the gap therefore redelivers, which
/// is the failure worth having: a duplicate is visible at the receiver and a receiver can be
/// idempotent, whereas a drop is invisible to everybody forever.
///
/// There is no lease column and no claim. Part A's notes describe one for concurrent workers,
/// and Kairos has exactly one process running exactly one of these, so there is nothing to
/// contend with — a lease that never contends is a column, a query and a stale-lease recovery
/// path all maintained for a scenario that does not exist. If this ever runs two replicas, the
/// table is already the right shape to add one.
/// </remarks>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopes,
    IOptions<WebhookOptions> options,
    IClock clock,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    /// <summary>
    /// Shorter than the poller's minute, because this one bounds how long a booking waits to be
    /// announced and a booking is announced the instant it is made. Ten seconds is a delay a
    /// person reads as "immediately" and a tick that finds nothing costs one indexed query
    /// against a column whose whole purpose is that query. Not shorter: the outbox is a queue,
    /// not a bus, and a tighter loop would buy latency nobody is measuring.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many deliveries one tick will attempt. Bounded so a backlog — a receiver that was
    /// down for a day, or a first URL configured after months of queueing — drains over
    /// successive ticks instead of holding one scope and one DbContext open for hours.
    /// </summary>
    private const int BatchSize = 100;

    /// <summary>
    /// Enough of a failure to act on. The column is diagnostic, and an unbounded one is a
    /// provider stack trace per attempt in a database that is meant to stay small.
    /// </summary>
    private const int MaxErrorLength = 500;

    private readonly WebhookOptions _options = options.Value;

    /// <summary>
    /// One client for the life of the process, which for a single fixed receiver is exactly
    /// right: a client per delivery would leak sockets in TIME_WAIT under any backlog at all.
    /// The timeout is the configured one, so a black hole costs a delivery rather than a tick.
    /// </summary>
    private readonly HttpClient _http = new() { Timeout = options.Value.Timeout };

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(_options.Url))
        {
            // Said once and then gone. Returning ends the service, so an unconfigured Kairos —
            // which is every fresh one — has no timer, no query and no log line a minute for
            // work that cannot exist. Messages keep queueing: the outbox is the record that
            // something happened, and subscribing later is allowed to mean receiving the backlog.
            logger.LogInformation(
                "No {Section}:Url is configured, so nothing is subscribed and the outbox is not "
                + "drained. Messages continue to be queued.",
                WebhookOptions.SectionName);
            return;
        }

        if (string.IsNullOrEmpty(_options.Secret))
        {
            // An error rather than a shrug, and still not a crash. Delivering unsigned would hand
            // a receiver bodies it cannot tell from anyone else's, which is the single thing the
            // signature exists to prevent — so the honest answer is to deliver nothing and say
            // why, loudly, at startup.
            logger.LogError(
                "{Section}:Url is set but {Section}:Secret is not. Nothing will be delivered: an "
                + "unsigned webhook is one a receiver cannot tell from a forgery.",
                WebhookOptions.SectionName,
                WebhookOptions.SectionName);
            return;
        }

        var signer = new WebhookSigner(_options.Secret);
        using var timer = new PeriodicTimer(TickInterval);

        // Drained at startup before the first wait: whatever was queued while the process was
        // down has already waited longer than any tick interval.
        while (await TickAsync(timer, signer, stoppingToken))
        {
        }
    }

    /// <summary>Returns false when the host is shutting down.</summary>
    private async Task<bool> TickAsync(
        PeriodicTimer timer,
        WebhookSigner signer,
        CancellationToken stoppingToken)
    {
        try
        {
            await DrainAsync(signer, stoppingToken);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            // Ground rule 14, applied to the queue rather than to a booking: nothing a receiver
            // or the database does may take the dispatcher down, because a dispatcher that is
            // gone is a silent drop of everything after it.
            logger.LogError(exception, "The outbox drain failed. Retrying on the next tick.");
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

    private async Task DrainAsync(WebhookSigner signer, CancellationToken cancellationToken)
    {
        // A scope per tick. The context is tracking the rows this tick is about to mark, and
        // holding one across ticks would keep every message ever dispatched alive in the change
        // tracker — quite apart from DbContext not being thread-safe and the poller ticking
        // alongside on its own schedule.
        using var scope = scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<KairosDbContext>();

        var maxAttempts = _options.MaxAttempts;

        // Exhausted rows leave the queue rather than staying in it and being skipped: a filter
        // that only decided not to send would still re-read every dead message on every tick for
        // the life of the deployment. The row keeps its Attempts and its LastError, which is
        // where an operator reads what happened.
        var pending = await context.OutboxMessages
            .Where(message => message.DispatchedAt == null && message.Attempts < maxAttempts)
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var message in pending)
        {
            await DeliverAsync(context, signer, message, cancellationToken);
        }
    }

    /// <summary>
    /// Saved either way: a success writes <see cref="OutboxMessage.DispatchedAt"/>, a failure
    /// writes the attempt and the reason and leaves the row for the next tick.
    /// </summary>
    private async Task DeliverAsync(
        KairosDbContext context,
        WebhookSigner signer,
        OutboxMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            await PostAsync(signer, message.Payload, cancellationToken);

            // Only here. Marking before the send would turn a receiver that never answered into
            // a message nobody will ever try again.
            message.DispatchedAt = clock.GetCurrentInstant().ToUnixTimeSeconds();
            message.LastError = null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Also catches the client's own timeout, which arrives as a TaskCanceledException
            // with our token untriggered — a receiver that hangs is a failed attempt, not a
            // shutdown.
            message.Attempts++;
            message.LastError = Truncate(exception.Message);

            if (message.Attempts >= _options.MaxAttempts)
            {
                logger.LogError(
                    exception,
                    "Giving up on outbox message {Message} for booking {Booking} after {Attempts} "
                    + "attempts. It will not be retried.",
                    message.Id,
                    message.BookingId,
                    message.Attempts);
            }
            else
            {
                logger.LogWarning(
                    exception,
                    "Delivery of outbox message {Message} failed (attempt {Attempts}). Retrying "
                    + "on a later tick.",
                    message.Id,
                    message.Attempts);
            }
        }

        // Uncancellable: this records work a third party has already done or refused, and a
        // shutdown between the POST and the mark is precisely the gap that causes a redelivery.
        await context.SaveChangesAsync(CancellationToken.None);
    }

    private async Task PostAsync(WebhookSigner signer, string payload, CancellationToken cancellationToken)
    {
        var timestamp = clock.GetCurrentInstant().ToUnixTimeSeconds();

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Url)
        {
            // The bytes on the wire and the bytes that were signed are the same UTF-8 rendering
            // of the same string, which is the whole requirement: a receiver verifies the raw
            // body it received, so anything that re-serialised here would break every signature.
            Content = new StringContent(payload, Encoding.UTF8, MediaTypeNames.Application.Json),
        };

        request.Headers.Add(
            WebhookSigner.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(WebhookSigner.SignatureHeader, signer.Sign(timestamp, payload));

        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static string Truncate(string message) =>
        message.Length <= MaxErrorLength ? message : message[..MaxErrorLength];
}
