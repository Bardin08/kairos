using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using NodaTime;

namespace Kairos.Api.Security;

/// <summary>
/// One fixed window per caller per class, spent one request at a time. Owning the count is what
/// makes the answer and the headers the same number: a framework limiter keeps it where the
/// application cannot read it.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="MemoryCache"/> entry per partition, expiring when its window ends, so eviction is
/// the expiry. Nothing sweeps, and nothing has to be told when a caller goes away.
/// </para>
/// <para>
/// The count is approximate under concurrency, in the direction of letting a request through: two
/// requests missing the cache at the same instant both build a counter, one wins the entry, and the
/// loser's increment lands on an object nobody reads. At most one extra request per concurrent
/// first-arrival, in the opening instant of a window. Exactness would cost a lock on the hot path
/// of every anonymous request.
/// </para>
/// </remarks>
public sealed class RateLimitBudget(IClock clock, ILogger<RateLimitBudget> logger) : IDisposable
{
    /// <summary>
    /// How many callers are counted at once. Entries retire with their windows, so this bounds a
    /// flood arriving faster than they retire rather than being a working limit — ten thousand
    /// addresses in one window is a crawler. At the ceiling the cache evicts rather than refusing,
    /// so an evicted caller gets a fresh window with its full allowance. The alternative is letting
    /// an address-rotating flood grow the process until it is killed.
    /// </summary>
    private const long MaxTrackedCallers = 10_000;

    /// <summary>Every entry counts as one against <see cref="MaxTrackedCallers"/>.</summary>
    private const int OneCaller = 1;

    private const int NoneLeft = 0;

    private readonly MemoryCache _windows = new(
        Options.Create(new MemoryCacheOptions { SizeLimit = MaxTrackedCallers }));

    /// <summary>Set on the first request from a connection with no peer, and never cleared.</summary>
    private int _warnedAboutMissingPeer;

    /// <summary>
    /// Charges one request to <paramref name="partition"/> and says what that leaves. The window
    /// begins at the first request charged to it and ends <paramref name="window"/> later whatever
    /// happens in between, so the remaining count the headers publish is a number the caller can
    /// act on rather than a sliding figure nobody can.
    /// </summary>
    public RateLimitReading Spend(string partition, int permits, TimeSpan window)
    {
        var now = clock.GetCurrentInstant();
        var expires = now + Duration.FromTimeSpan(window);

        var counter = _windows.GetOrCreate(partition, entry =>
        {
            entry.AbsoluteExpiration = expires.ToDateTimeOffset();
            entry.Size = OneCaller;
            return new Counter(expires);
        })!;

        var used = counter.Increment();

        return new RateLimitReading(
            used <= permits,
            permits,
            Math.Max(NoneLeft, permits - used),
            (counter.Expires - now).ToTimeSpan());
    }

    /// <summary>
    /// Once for the life of this application: a listener with no peer is a deployment shape, and a
    /// line per request would be a second denial of service on a host already flooded.
    /// </summary>
    /// <remarks>
    /// Here rather than beside the middleware that calls it, which is static: a static flag is
    /// tripped by whichever application in the process met the case first, leaving every other one
    /// silent — in a test run, whichever suite went first.
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

    public void Dispose() => _windows.Dispose();

    /// <summary>One window's tally, and when it stops counting.</summary>
    private sealed class Counter(Instant expires)
    {
        private int _used;

        public Instant Expires { get; } = expires;

        public int Increment() => Interlocked.Increment(ref _used);
    }
}
