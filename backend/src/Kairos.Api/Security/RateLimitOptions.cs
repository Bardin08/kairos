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
    /// sized against the deployment rather than against the traffic: the reference instance runs at
    /// <c>limits 192Mi</c> and its last reading was 190.2 MiB (<c>docs/measurements.md</c>), so the
    /// headroom this may spend is under two megabytes — about a thousand entries at the few hundred
    /// bytes a key, a window and a limiter cost. A booking link meeting a thousand distinct
    /// addresses inside one window is being crawled rather than read, and the asymmetry settles the
    /// rest: too low costs a caller its <c>RateLimit-*</c> headers, too high costs the pod.
    /// </summary>
    public const int DefaultMaxPartitions = 1_000;

    public int WritePermits { get; set; } = DefaultWritePermits;

    public int ReadPermits { get; set; } = DefaultReadPermits;

    public TimeSpan Window { get; set; } = DefaultWindow;

    public int MaxPartitions { get; set; } = DefaultMaxPartitions;
}
