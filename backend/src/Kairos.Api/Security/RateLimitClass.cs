namespace Kairos.Api.Security;

/// <summary>
/// The budget an endpoint spends from, carried as endpoint metadata so every anonymous route
/// states its class where it is mapped and one middleware after routing reads it back. Two classes
/// rather than one per route: what matters is the kind of endpoint — a write with side effects the
/// caller does not pay for, and a read that materializes a month of intervals per call.
/// </summary>
/// <param name="Name">
/// Goes into the partition key, so one caller's reads and writes are counted apart rather than out
/// of a single allowance.
/// </param>
/// <param name="Permits">Reads this class's allowance out of the options.</param>
public sealed record RateLimitClass(string Name, Func<RateLimitOptions, int> Permits)
{
    /// <summary>A request that writes, mails, or reaches a provider on the caller's behalf.</summary>
    public static readonly RateLimitClass AnonymousWrite =
        new("anonymous-write", options => options.WritePermits);

    /// <summary>A request that only reads, but computes a month of intervals to do it.</summary>
    public static readonly RateLimitClass AnonymousRead =
        new("anonymous-read", options => options.ReadPermits);
}
