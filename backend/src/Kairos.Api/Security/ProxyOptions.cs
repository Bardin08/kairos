namespace Kairos.Api.Security;

/// <summary>
/// What the deployment puts in front of Kairos, and how much of what it says is believed. Three
/// paths and a default, named here rather than in <c>Program.cs</c> so that the tests asserting
/// them break at compile time when one is renamed instead of at run time.
/// </summary>
public static class ProxyOptions
{
    /// <summary>
    /// How many proxies deep the real caller sits. Every hop beyond this one is left in the header
    /// and never resolved, which is safe; too few is not, because the caller then resolves to a
    /// proxy and every caller on earth becomes one rate-limit partition.
    /// </summary>
    public const string ForwardLimitPath = "Proxy:ForwardLimit";

    /// <summary>The addresses allowed to speak for a caller, one per entry.</summary>
    public const string KnownProxiesPath = "Proxy:KnownProxies";

    /// <summary>The same permission granted to a range, as CIDR.</summary>
    public const string KnownNetworksPath = "Proxy:KnownNetworks";

    /// <summary>
    /// One reverse proxy, which is the shape a self-hosted deployment has unless it says
    /// otherwise. It is also the framework's own default, restated here because the deployment
    /// this decision is wrong for — a tunnel in front of that proxy — is a supported one.
    /// </summary>
    public const int DefaultForwardLimit = 1;
}
