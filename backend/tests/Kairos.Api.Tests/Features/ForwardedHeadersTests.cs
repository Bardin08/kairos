using System.Globalization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// How far down the chain of proxies the real caller is, and what happens to the settings that say
/// so when they are written wrong. Both are read once, while the pipeline is being built, and
/// neither says anything at request time — which is why they are pinned here.
/// </summary>
/// <remarks>
/// The number matters because the reference deployment is a tunnel in front of a reverse proxy, so
/// <c>X-Forwarded-For</c> arrives as the invitee followed by the tunnel's own hop. Stopping one
/// hop short of the invitee resolves every caller on earth to one address, which merges the whole
/// internet into a single rate-limit bucket — silently, in exactly the deployment shape this
/// suite's settings exist to serve.
/// </remarks>
public class ForwardedHeadersTests
{
    private const string ForwardLimitKey = "Proxy:ForwardLimit";

    private const string KnownProxiesKey = "Proxy:KnownProxies";

    private const string KnownNetworksKey = "Proxy:KnownNetworks";

    private const string FirstKnownProxyKey = $"{KnownProxiesKey}:0";

    private const string FirstKnownNetworkKey = $"{KnownNetworksKey}:0";

    /// <summary>One reverse proxy and nothing in front of it, which is the unconfigured shape.</summary>
    private const int SingleProxy = 1;

    /// <summary>A tunnel in front of that proxy: two hops of the header belong to the deployment.</summary>
    private const int TunnelledProxy = 2;

    /// <summary>
    /// A service name where an address belongs — what a compose file's own vocabulary invites, and
    /// the mistake that has to name itself rather than arrive as "an invalid IP address".
    /// </summary>
    private const string MalformedProxy = "traefik";

    /// <summary>An address where a network belongs: correct in the setting one line above it.</summary>
    private const string MalformedNetwork = "10.0.0.0";

    [Fact]
    public void ForwardLimit_Unconfigured_StopsAtASingleProxy()
    {
        using var app = new KairosApp();

        ForwardedHeadersOf(app.Services).ForwardLimit.ShouldBe(SingleProxy);
    }

    [Fact]
    public void ForwardLimit_Configured_IsTheConfiguredNumberOfHops()
    {
        using var app = new KairosApp();
        var host = app.WithWebHostBuilder(
            builder => builder.UseSetting(ForwardLimitKey, Text(TunnelledProxy)));

        ForwardedHeadersOf(host.Services).ForwardLimit.ShouldBe(TunnelledProxy);
    }

    /// <summary>
    /// A trusted proxy that does not parse stops the process, and says which setting and which
    /// value did it. The framework's own message names neither, and this is read by somebody
    /// watching a container refuse to start with nothing else to go on.
    /// </summary>
    [Fact]
    public void CreatingTheHost_WithAMalformedKnownProxy_FailsAndNamesTheSettingAndTheValue()
    {
        using var app = new KairosApp();

        var thrown = Should.Throw<Exception>(() => app
            .WithWebHostBuilder(builder => builder.UseSetting(FirstKnownProxyKey, MalformedProxy))
            .CreateClient());

        thrown.ToString().ShouldContain(FirstKnownProxyKey);
        thrown.ToString().ShouldContain(MalformedProxy);
    }

    /// <summary>
    /// The same for the other list, which takes CIDR rather than an address — so the two settings
    /// accept different spellings of the same idea, and the one written into the wrong of them is
    /// the mistake the message has to be able to point at.
    /// </summary>
    [Fact]
    public void CreatingTheHost_WithAMalformedKnownNetwork_FailsAndNamesTheSettingAndTheValue()
    {
        using var app = new KairosApp();

        var thrown = Should.Throw<Exception>(() => app
            .WithWebHostBuilder(builder => builder.UseSetting(FirstKnownNetworkKey, MalformedNetwork))
            .CreateClient());

        thrown.ToString().ShouldContain(FirstKnownNetworkKey);
        thrown.ToString().ShouldContain(MalformedNetwork);
    }

    private static ForwardedHeadersOptions ForwardedHeadersOf(IServiceProvider services) =>
        services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
