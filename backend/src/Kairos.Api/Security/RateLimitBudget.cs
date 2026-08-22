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
/// Held to roughly <see cref="RateLimitOptions.MaxPartitions"/> entries, because unbounded it
/// would be worse than the problem it helps solve: one entry per source address, strongly
/// referenced and never released, is reachable through an anonymous <c>POST /bookings</c>, so a
/// caller rotating addresses would trade this API's mail-relay amplification for memory
/// exhaustion. Roughly, and not exactly — the count is read and acted on without a lock, so
/// requests in flight together can overshoot it. The overshoot is bounded by how many of them
/// there are, costs a few hundred bytes each, and the next sweep takes it back; a counter precise
/// enough to say otherwise would cost more than the thing it guards.
/// </para>
/// <para>
/// Reclamation follows the framework's idleness policy rather than inventing one. A partition it
/// has dropped has had its limiter disposed, which is what <see cref="Statistics"/> reports as
/// null, and that is the only condition under which an entry is removed. So the cap limits how
/// many callers are remembered at once and not how many a process may ever serve, and no entry
/// belonging to a caller still being scored can be taken away.
/// </para>
/// <para>
/// What none of this does is refuse or delay a request. Past the cap the entry is simply not
/// recorded, <see cref="Read"/> answers null for it, and no <c>RateLimit-*</c> header is written.
/// Enforcement lives in the framework's own partition table rather than in this one, so such a
/// caller is throttled exactly as before — it just stops being told where it stands. The courtesy
/// headers are the only thing that degrades.
/// </para>
/// </remarks>
public sealed class RateLimitBudget(
    IOptions<RateLimitOptions> options,
    IClock clock,
    ILogger<RateLimitBudget> logger)
{
    /// <summary>
    /// The value of <see cref="_lastSweep"/> before any sweep has run, and a sentinel rather than
    /// an instant: the field holds ticks since the Unix epoch, so "zero" is a real reading a clock
    /// can return, and leaving the first sweep to fall out of the arithmetic would make
    /// reclamation depend on how far the wall clock happens to sit from 1970.
    /// </summary>
    private const long NeverSwept = 0;

    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

    /// <summary>When a sweep last started, as Unix ticks, or <see cref="NeverSwept"/> before one has.</summary>
    private long _lastSweep = NeverSwept;

    /// <summary>Set once, the first time a request arrives from a connection with no peer.</summary>
    private int _warnedAboutMissingPeer;

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

        var capacity = options.Value.MaxPartitions;
        if (_windows.Count >= capacity)
        {
            SweepIfDue(length);
        }

        // Recorded only if there is room, and the limiter is returned either way — see the third
        // paragraph above for what a caller past the cap does and does not lose.
        if (_windows.Count < capacity)
        {
            _windows[partition] = window;
        }

        return limiter;
    }

    /// <summary>
    /// Null when the partition is not held — a request the limiter never scored, one that arrived
    /// past the cap and was deliberately not recorded, or one the framework has since dropped.
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
    /// Said once for the life of this application, not once per request: a listener with no peer is
    /// a deployment shape rather than an event, and a line per request would be a second denial of
    /// service on a host already being flooded.
    /// </summary>
    /// <remarks>
    /// Here rather than beside the partitioner that calls it, because this object is one per
    /// application while that code is static. A static flag would be tripped by whichever
    /// application in the process met the case first and leave every other one silent — which in a
    /// test run means the assertion lands on whichever suite happened to go first.
    /// </remarks>
    public void WarnAboutMissingPeerOnce()
    {
        if (Interlocked.Exchange(ref _warnedAboutMissingPeer, 1) != 0)
        {
            return;
        }

        logger.LogWarning(
            "A rate-limited request arrived with no remote address, so every caller shares one "
            + "budget and X-Forwarded-For cannot be trusted either. This is what a listener on a "
            + "Unix domain socket or a named pipe looks like: have Kestrel listen on a TCP port "
            + "and name the proxy under Proxy:KnownProxies to get callers limited apart.");
    }

    /// <summary>
    /// At most one sweep <em>started</em> per window, and at most one thread's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bounded by time rather than by how many entries it looks at, which is what makes it
    /// complete. A sweep that stopped after a fixed number of candidates would examine the same
    /// ones every time — a <see cref="ConcurrentDictionary{TKey, TValue}"/> enumerates in bucket
    /// order, so the prefix it walks is the same prefix — and if none of those were reclaimable it
    /// would free nothing, forever, with reclaimable entries sitting past where it stopped. The
    /// gate is what makes scanning all of them affordable: under the address-rotating flood this
    /// exists for every request arrives over capacity, so an ungated sweep would have every thread
    /// in the process walking the whole dictionary at once. Losing the exchange means another
    /// thread already has it, and this one has nothing to do.
    /// </para>
    /// <para>
    /// Started, and not finished: the stamp is taken before the scan, so a scan lasting longer
    /// than a window lets the next one begin beside it. That is deliberate rather than overlooked
    /// — the alternative is holding the gate across the scan, which turns a slow sweep into a
    /// stalled reclaim — and it is harmless, because removing an entry twice is
    /// <see cref="ConcurrentDictionary{TKey, TValue}.TryRemove(TKey, out TValue)"/> returning
    /// false the second time.
    /// </para>
    /// </remarks>
    private void SweepIfDue(TimeSpan length)
    {
        var now = clock.GetCurrentInstant().ToUnixTimeTicks();
        var last = Interlocked.Read(ref _lastSweep);
        var since = now - last;

        // Three ways to be due, and only the middle one is the ordinary case. Before the first
        // sweep the stamp is a sentinel rather than an instant, so it is compared and not
        // subtracted. And a clock stepped backwards — NTP correcting a drifting Pi — leaves a
        // stamp in the future, which unhandled would read as "swept moments ago" and close this
        // gate until real time caught back up with it; a negative interval is treated as due, and
        // the exchange below restamps to the corrected reading.
        if ((last != NeverSwept && since >= 0 && since < length.Ticks)
            || Interlocked.CompareExchange(ref _lastSweep, now, last) != last)
        {
            return;
        }

        foreach (var (partition, window) in _windows)
        {
            // The framework's own signal and never one of ours: it disposes the limiter of a
            // partition it has decided is idle, and a disposed limiter is what Statistics reports
            // as null. A limiter it still holds belongs to a caller it is still scoring, and
            // evicting that entry would take the caller's headers away for the rest of its
            // session — the factory above only runs again on a miss in the framework's table, and
            // for a caller it is still tracking there would not be one.
            if (Statistics(window.Limiter) is null)
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
