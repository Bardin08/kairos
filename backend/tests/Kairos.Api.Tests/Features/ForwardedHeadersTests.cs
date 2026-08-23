using System.Globalization;
using System.Net;

using Kairos.Api.Security;
using Kairos.Api.Shared;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
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
    private const string FirstKnownProxyKey = $"{ProxyOptions.KnownProxiesPath}:0";

    private const string FirstKnownNetworkKey = $"{ProxyOptions.KnownNetworksPath}:0";

    /// <summary>
    /// One reverse proxy and nothing in front of it, which is the unconfigured shape. Written out
    /// rather than read from <see cref="ProxyOptions.DefaultForwardLimit"/>: a test that takes its
    /// expectation from the value under test agrees with every change to it.
    /// </summary>
    private const int SingleProxy = 1;

    /// <summary>A tunnel in front of that proxy: two hops of the header are the deployment's.</summary>
    private const int TunnelledProxy = 2;

    /// <summary>The caller the whole chain exists to carry, from the documentation ranges.</summary>
    private const string Invitee = "203.0.113.9";

    /// <summary>The hop that appended the invitee, and what the connection claims to be.</summary>
    private const string Tunnel = "198.51.100.4";

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
            builder => builder.UseSetting(ProxyOptions.ForwardLimitPath, Text(TunnelledProxy)));

        ForwardedHeadersOf(host.Services).ForwardLimit.ShouldBe(TunnelledProxy);
    }

    /// <summary>
    /// What the number above buys, driven rather than read: the same two-hop header, and the
    /// caller the middleware settles on at each depth.
    /// </summary>
    /// <remarks>
    /// The rows are the two deployments. At one hop the header's rightmost entry is consumed and
    /// the caller IS the tunnel — every invitee on earth arriving as one address, which is the
    /// failure this setting exists to avert and which nothing else in this suite would notice. At
    /// two, with the tunnel trusted, the invitee behind it is resolved. The tunnel is named in
    /// <see cref="ProxyOptions.KnownProxiesPath"/> for both rows, because a depth of two with
    /// nothing trusted stops at the first untrusted hop regardless of the limit — which is the
    /// half that keeps a caller from prepending whatever address it likes.
    /// </remarks>
    [Theory]
    [InlineData(SingleProxy, Tunnel)]
    [InlineData(TunnelledProxy, Invitee)]
    public async Task TheResolvedCaller_BehindATunnelAndAProxy_IsAsDeepAsTheForwardLimitReaches(
        int forwardLimit,
        string expected)
    {
        using var app = new KairosApp();
        var caller = new ForwardedCaller();

        using var client = app
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting(ProxyOptions.ForwardLimitPath, Text(forwardLimit));
                builder.UseSetting(FirstKnownProxyKey, Tunnel);
                builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(caller));
            })
            .CreateClient();

        client.DefaultRequestHeaders.Add(
            ForwardedHeadersDefaults.XForwardedForHeaderName, $"{Invitee}, {Tunnel}");

        var response = await client.GetAsync(
            $"{ApiVersions.V1}/sign-in", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        caller.Resolved.ShouldBe(IPAddress.Parse(expected));
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

    /// <summary>
    /// The peer the in-memory host does not have, and the caller the middleware settled on.
    /// TestServer opens no socket, so <c>Connection.RemoteIpAddress</c> is null and
    /// <c>UseForwardedHeaders</c> takes no header's word for who is calling — with nothing to
    /// check, it does nothing at all. Loopback is what the framework trusts by default, so lending
    /// the connection that address is the whole substitution.
    /// </summary>
    /// <remarks>
    /// One middleware for both halves, and an <see cref="IStartupFilter"/> because this has to run
    /// in front of <c>UseForwardedHeaders</c>, which is the application's own first line. The
    /// address is read on the way back out: the middleware rewrites
    /// <c>Connection.RemoteIpAddress</c> in place, so what this sees after awaiting is what every
    /// line of the pipeline below it saw — the rate limiter's partition included.
    /// </remarks>
    private sealed class ForwardedCaller : IStartupFilter
    {
        public IPAddress? Resolved { get; private set; }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, forward) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.IPv6Loopback;
                await forward(context);
                Resolved = context.Connection.RemoteIpAddress;
            });

            next(app);
        };
    }
}
