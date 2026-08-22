using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using NodaTime;

namespace Kairos.Api.Security;

/// <summary>
/// The count itself: one fixed window per caller per class, spent one request at a time. Owning it
/// rather than mirroring a framework limiter is what makes the answer and the headers the same
/// number — the alternative keeps the count somewhere the application cannot read and leaves it
/// reconstructing one beside it.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="MemoryCache"/> entry per partition, expiring when its window ends, so the eviction
/// is the expiry: an entry lives exactly as long as the count it holds is meaningful, and a caller
/// that stops calling stops costing anything one window later. Nothing sweeps and nothing has to
/// be told when a caller goes away.
/// </para>
/// <para>
/// The count is approximate under concurrency, in the direction of letting a request through: two
/// requests that miss the cache at the same instant both build a counter, one of them wins the
/// entry, and the loser's increment lands on an object nobody reads. It costs the caller's own
/// allowance at most one extra request per concurrent first-arrival, only in the opening instant
/// of a window, and it corrects itself as soon as the entry is shared. A count exact under
/// contention is a lock on the hot path of every anonymous request, which is a worse trade than
/// the eleventh booking attempt occasionally getting through.
/// </para>
/// </remarks>
public sealed class RateLimitBudget(IClock clock, ILogger<RateLimitBudget> logger) : IDisposable
{
    /// <summary>
    /// How many callers are counted at once. Entries retire themselves as their windows end, so
    /// this is a ceiling for a flood arriving faster than they retire rather than a working limit —
    /// ten thousand distinct addresses inside one window is a crawler, not a booking page. At the
    /// ceiling the cache evicts by its own policy rather than refusing anyone, and a caller whose
    /// entry is evicted starts a fresh window with its full allowance back. That is the honest cost
    /// of bounding this, and it is the right way round: the alternative to occasionally forgiving a
    /// caller is letting an address-rotating flood grow the process until it is killed.
    /// </summary>
    private const long MaxTrackedCallers = 10_000;

    /// <summary>Every entry counts as one against <see cref="MaxTrackedCallers"/>.</summary>
    private const int OneCaller = 1;

    private const int NoneLeft = 0;

    private readonly MemoryCache _windows = new(
        Options.Create(new MemoryCacheOptions { SizeLimit = MaxTrackedCallers }));

    /// <summary>Set once, the first time a request arrives from a connection with no peer.</summary>
    private int _warnedAboutMissingPeer;

    /// <summary>
    /// Charges one request to <paramref name="partition"/> and says what that leaves. The window
    /// begins at the first request charged to it and ends <paramref name="window"/> later,
    /// whatever happens in between — a fixed window, so the remaining count the headers publish is
    /// a number the caller can act on rather than a sliding figure nobody can.
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
    /// Said once for the life of this application, not once per request: a listener with no peer is
    /// a deployment shape rather than an event, and a line per request would be a second denial of
    /// service on a host already being flooded.
    /// </summary>
    /// <remarks>
    /// Here rather than beside the middleware that calls it, because this object is one per
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

    public void Dispose() => _windows.Dispose();

    /// <summary>One window's tally, and when it stops counting.</summary>
    private sealed class Counter(Instant expires)
    {
        private int _used;

        public Instant Expires { get; } = expires;

        public int Increment() => Interlocked.Increment(ref _used);
    }
}
