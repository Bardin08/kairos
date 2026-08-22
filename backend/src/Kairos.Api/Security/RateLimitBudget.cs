using System.Collections.Concurrent;
using System.Threading.RateLimiting;

using NodaTime;

namespace Kairos.Api.Security;

/// <summary>
/// The limiters, kept so their counts can be published. ASP.NET Core hands the lease to its own
/// middleware and never to the application, so <c>RateLimit-Remaining</c> on an accepted response
/// cannot be read from it — and a counter kept beside the limiter would be a second source of
/// truth that drifts from the first. This holds the limiter instead and reads its statistics.
/// </summary>
public sealed class RateLimitBudget(IClock clock)
{
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

    /// <summary>
    /// Called by the partitioner when a partition is met for the first time, and again if the
    /// framework has dropped an idle one since. The entry is overwritten rather than merged: the
    /// newest limiter is the live one, and a stale entry would publish a count nobody is spending.
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

        _windows[partition] = new Window(limiter, permits, length, clock.GetCurrentInstant());
        return limiter;
    }

    /// <summary>Null when the partition has not been seen — a request the limiter never scored.</summary>
    public RateLimitReading? Read(string partition)
    {
        if (!_windows.TryGetValue(partition, out var window))
        {
            return null;
        }

        var statistics = window.Limiter.GetStatistics();
        if (statistics is null)
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

    private sealed record Window(
        FixedWindowRateLimiter Limiter,
        int Permits,
        TimeSpan Length,
        Instant Started);
}
