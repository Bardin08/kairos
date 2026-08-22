namespace Kairos.Api.Security;

/// <summary>
/// How much an anonymous caller may ask for. Two numbers rather than one per route: what matters
/// is the class of endpoint — a write with side effects the caller does not pay for, and a read
/// that materializes a month of intervals per call.
/// </summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimits";

    /// <summary>
    /// A person books once, twice if they mistyped an address. Ten in a minute is already a
    /// script, and every one of them sends mail to an address the caller chose.
    /// </summary>
    public const int DefaultWritePermits = 10;

    /// <summary>
    /// The booking page loads a link and then a month of slots, and moves a month at a time. Sixty
    /// a minute is a person clicking through a year; a crawler passes it in seconds.
    /// </summary>
    public const int DefaultReadPermits = 60;

    /// <summary>
    /// One minute, and fixed rather than sliding: the window a caller can reason about from the
    /// headers is the one worth publishing, and a sliding window's remaining count is a number
    /// nobody can act on.
    /// </summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How many partitions are remembered so their counts can be published. Small on purpose, and
    /// sized against the process rather than against the traffic. The last reading was a 190.2 MiB
    /// high-water mark against a target of under 100 Mi that it already misses, and there is no
    /// container image or manifest in this repository to attach a limit to
    /// (<c>docs/measurements.md</c>), so the honest position is that the headroom is unknown and a
    /// courtesy feature should ask for very little of it. The marginal cost of an entry is its key
    /// and a small record — the limiter is the framework's and exists either way — so a thousand of
    /// them is a couple of hundred kilobytes, and that is as precisely as it is worth deriving. A
    /// booking link meeting a thousand distinct addresses inside one window is being crawled rather
    /// than read, and the asymmetry settles the rest: too low costs a caller its
    /// <c>RateLimit-*</c> headers, too high costs the process.
    /// </summary>
    public const int DefaultMaxPartitions = 1_000;

    public int WritePermits { get; set; } = DefaultWritePermits;

    public int ReadPermits { get; set; } = DefaultReadPermits;

    public TimeSpan Window { get; set; } = DefaultWindow;

    public int MaxPartitions { get; set; } = DefaultMaxPartitions;
}
