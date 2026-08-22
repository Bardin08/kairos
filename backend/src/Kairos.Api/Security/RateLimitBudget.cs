using System.Collections.Concurrent;
using System.Threading.RateLimiting;

using Microsoft.Extensions.Options;

using NodaTime;

namespace Kairos.Api.Security;

/// <summary>
/// The limiters, kept so their counts can be published. ASP.NET Core hands the lease to its own
/// middleware and never to the application, so <c>RateLimit-Remaining</c> on an accepted response
/// cannot be read from it — and a counter kept beside the limiter would be a second source of
/// truth that drifts from the first. This holds the limiter instead and reads its statistics.
/// </summary>
/// <remarks>
/// <para>
/// Bounded by <see cref="RateLimitOptions.MaxPartitions"/>, because unbounded it would be worse
/// than the problem it helps solve. One entry per source address, strongly referenced and never
/// released, is reachable through an anonymous <c>POST /bookings</c> — so a caller rotating
/// addresses would trade this API's mail-relay amplification for memory exhaustion, on a
/// deployment measured at 190.2 MiB against a 192Mi limit.
/// </para>
/// <para>
/// The bound is recoverable rather than permanent: a partition arriving at a full dictionary
/// first sweeps entries nobody is spending, so the cap limits how many callers are remembered at
/// once and not how many a process may ever serve.
/// </para>
/// <para>
/// What the bound never does is refuse or delay a request. Past the cap the entry is simply not
/// recorded, <see cref="Read"/> answers null for it, and no <c>RateLimit-*</c> header is written.
/// Enforcement lives in the framework's own partition table rather than in this one, so such a
/// caller is throttled exactly as before — it just stops being told where it stands. The courtesy
/// headers are the only thing that degrades.
/// </para>
/// </remarks>
public sealed class RateLimitBudget(IOptions<RateLimitOptions> options, IClock clock)
{
    /// <summary>
    /// How many entries one request may examine while sweeping. Bounded because the sweep runs
    /// inline on a request that is already waiting: walking a full dictionary would charge
    /// whichever caller happened to arrive first for every other caller's entry, which is the
    /// kind of cost this class exists to cap rather than to introduce.
    /// </summary>
    private const int SweepBudget = 64;

    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

    private readonly int _capacity = options.Value.MaxPartitions;

    /// <summary>
    /// Called by the partitioner when a partition is met for the first time, and again if the
    /// framework has dropped an idle one since.
    /// </summary>
    public RateLimiter Limiter(string partition, int permits, TimeSpan length)
    {
        var limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = length,
            // Nothing waits for a permit. A queued booking request is a caller holding a
            // connection open to be told no a second later, which is the denial-of-service being
            // defended against wearing a politer response code.
            QueueLimit = 0,
            AutoReplenishment = true,
        });

        var window = new Window(limiter, permits, length, clock.GetCurrentInstant());

        // A partition already recorded is replaced whatever the count, and is not growth: the
        // newest limiter is the live one, and the entry it replaces would otherwise publish a
        // count nobody is spending, off a limiter the framework has already disposed.
        if (_windows.ContainsKey(partition))
        {
            _windows[partition] = window;
            return limiter;
        }

        if (_windows.Count >= _capacity)
        {
            Sweep();
        }

        // Recorded only if there is room, and the limiter is returned either way — see the third
        // paragraph above for what a caller past the cap does and does not lose.
        if (_windows.Count < _capacity)
        {
            _windows[partition] = window;
        }

        return limiter;
    }

    /// <summary>
    /// Null when the partition is not held — a request the limiter never scored, or one that
    /// arrived past the cap and was deliberately not recorded.
    /// </summary>
    public RateLimitReading? Read(string partition)
    {
        if (!_windows.TryGetValue(partition, out var window))
        {
            return null;
        }

        if (Statistics(window.Limiter) is not { } statistics)
        {
            return null;
        }

        // The window replenishes on a fixed period from the moment the limiter was built, so how
        // far into the current one we are is the elapsed time modulo its length. Read from IClock
        // like every other instant in this application.
        var elapsed = clock.GetCurrentInstant() - window.Started;
        var into = TimeSpan.FromTicks(elapsed.BclCompatibleTicks % window.Length.Ticks);

        return new RateLimitReading(
            window.Permits,
            (int)statistics.CurrentAvailablePermits,
            window.Length - into);
    }

    /// <summary>
    /// Drops entries nobody is spending, so a long-running process does not end up holding one per
    /// caller it has ever met.
    /// </summary>
    /// <remarks>
    /// Idle is both halves: the budget is back to full AND the window has turned over at least
    /// once since the limiter was built. Full permits alone would drop a partition in the instant
    /// between its limiter being built and its first lease being taken, when the budget is briefly
    /// untouched — a concurrent caller sweeping just then would evict an entry a request is in the
    /// middle of using.
    /// </remarks>
    private void Sweep()
    {
        var now = clock.GetCurrentInstant();
        var scanned = 0;

        foreach (var (partition, window) in _windows)
        {
            if (scanned++ == SweepBudget)
            {
                return;
            }

            // No statistics means the framework has disposed this limiter, which is the strongest
            // available statement that nobody is spending it.
            if (Statistics(window.Limiter) is not { } statistics
                || (statistics.CurrentAvailablePermits >= window.Permits
                    && now - window.Started >= Duration.FromTimeSpan(window.Length)))
            {
                _windows.TryRemove(partition, out _);
            }
        }
    }

    /// <summary>
    /// The limiter's own count, or null once the framework has disposed it — which it does to
    /// partitions that fall idle, and which is exactly the state an entry here can outlive.
    /// Measured rather than defensive: <c>FixedWindowRateLimiter.GetStatistics()</c> throws
    /// <see cref="ObjectDisposedException"/> instead of answering null, so a dropped limiter would
    /// otherwise surface as a 500 on whichever request swept past it.
    /// </summary>
    private static RateLimiterStatistics? Statistics(RateLimiter limiter)
    {
        try
        {
            return limiter.GetStatistics();
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private sealed record Window(
        FixedWindowRateLimiter Limiter,
        int Permits,
        TimeSpan Length,
        Instant Started);
}
