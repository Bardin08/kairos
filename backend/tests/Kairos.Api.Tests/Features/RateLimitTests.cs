using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Kairos.Api.Persistence;
using Kairos.Api.Security;
using Kairos.Api.Shared;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The anonymous write path, throttled. POST /bookings is the endpoint an unlimited caller turns
/// into an open relay: every accepted request mails a caller-supplied address with
/// caller-supplied prose in it, from the organizer's own relay and under their domain's
/// reputation, and burns their calendar quota on the way.
/// </summary>
public class RateLimitTests
{
    /// <summary>The shipping numbers, set back here because the harness raises them for everyone else.</summary>
    private const int WritePermits = RateLimitOptions.DefaultWritePermits;

    private const int ReadPermits = RateLimitOptions.DefaultReadPermits;

    private const string WritePermitsKey =
        $"{RateLimitOptions.SectionName}:{nameof(RateLimitOptions.WritePermits)}";

    private const string ReadPermitsKey =
        $"{RateLimitOptions.SectionName}:{nameof(RateLimitOptions.ReadPermits)}";

    private const string UnknownSlug = "no-such-link";

    /// <summary>Parsed rather than formatted, so nothing about the instant depends on the clock.</summary>
    private const string Start = "2026-09-07T09:00:00Z";

    private const string Day = "2026-09-07";

    private const int DurationMinutes = 30;
    private const string BookerName = "Vlad";
    private const string BookerEmail = "vlad@example.com";
    private const string BookerZone = "Europe/Warsaw";

    private const string MaxPartitionsKey =
        $"{RateLimitOptions.SectionName}:{nameof(RateLimitOptions.MaxPartitions)}";

    private const string WindowKey =
        $"{RateLimitOptions.SectionName}:{nameof(RateLimitOptions.Window)}";

    /// <summary>Three callers arriving through the same proxy, from the documentation ranges.</summary>
    private const string FirstCaller = "203.0.113.9";

    private const string SecondCaller = "198.51.100.4";

    /// <summary>Room for one caller, so the next one is the one past the cap.</summary>
    private const int RememberOne = 1;

    [Fact]
    public async Task CreateBooking_PastTheWindowsPermits_IsRefusedWith429AndRetryAfter()
    {
        using var app = new KairosApp();
        using var client = ShippingLimit(app);

        for (var spent = 0; spent < WritePermits; spent++)
        {
            var admitted = await AttemptAsync(client);
            admitted.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        var refused = await AttemptAsync(client);

        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        refused.Headers.RetryAfter.ShouldNotBeNull();

        var body = await refused.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        body.GetProperty(ProblemCode.Key).GetString().ShouldBe(RateLimitPolicies.TooManyRequestsCode);
    }

    [Fact]
    public async Task CreateBooking_UnderTheLimit_CountsTheRemainingBudgetDownOnEveryResponse()
    {
        using var app = new KairosApp();
        using var client = ShippingLimit(app);
        var spent = 0;

        var first = await AttemptAsync(client);
        spent++;

        HeaderOf(first, RateLimitPolicies.LimitHeader).ShouldBe(WritePermits);
        HeaderOf(first, RateLimitPolicies.RemainingHeader).ShouldBe(WritePermits - spent);

        // Somewhere inside the window that has just started, which is the only claim a caller can
        // act on and the one a dropped header would quietly stop making.
        HeaderOf(first, RateLimitPolicies.ResetHeader)
            .ShouldBeInRange(1, (int)RateLimitOptions.DefaultWindow.TotalSeconds);

        var second = await AttemptAsync(client);
        spent++;

        HeaderOf(second, RateLimitPolicies.RemainingHeader).ShouldBe(WritePermits - spent);
    }

    /// <summary>
    /// The read class, on the endpoint that is the cheap denial-of-service target on this API: one
    /// call materializes a month of intervals, nothing is written, and nobody is billed for it.
    /// Its budget is the larger one — a booking page really does load a link and then a month of
    /// slots — so this also pins that the two classes are two numbers and not one.
    /// </summary>
    [Fact]
    public async Task GetSlots_PastTheWindowsPermits_IsRefusedWith429()
    {
        using var app = new KairosApp();
        using var client = ShippingLimit(app);
        var slots = $"{ApiVersions.V1}/links/{UnknownSlug}/slots?from={Day}&to={Day}";

        for (var spent = 0; spent < ReadPermits; spent++)
        {
            var admitted = await client.GetAsync(slots, TestContext.Current.CancellationToken);
            admitted.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        var refused = await client.GetAsync(slots, TestContext.Current.CancellationToken);

        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    /// <summary>
    /// One caller's budget is not another's. Without this the partition could name nothing at all
    /// and every test above would still pass — a limiter that lumps every caller on earth into one
    /// bucket answers 429 exactly as convincingly, and is an outage rather than a limit.
    /// </summary>
    [Fact]
    public async Task CreateBooking_FromASecondForwardedCaller_SpendsASecondBudget()
    {
        using var app = new KairosApp();

        // One host, two clients: the limiter they are throttled by has to be the same one, or
        // this passes for the wrong reason.
        var host = ShippingLimitHost(app).WithWebHostBuilder(builder => builder.ConfigureServices(
            services => services.AddSingleton<IStartupFilter, LoopbackPeer>()));

        using var exhausted = ForwardedClient(host, FirstCaller);
        using var arriving = ForwardedClient(host, SecondCaller);

        for (var attempt = 0; attempt < WritePermits; attempt++)
        {
            await AttemptAsync(exhausted);
        }

        (await AttemptAsync(exhausted)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        var first = await AttemptAsync(arriving);

        // Admitted, and against a budget nothing has spent from but this one request.
        first.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        HeaderOf(first, RateLimitPolicies.RemainingHeader).ShouldBe(WritePermits - 1);
    }

    /// <summary>
    /// Past the cap a caller is still limited — it only stops being told where it stands. The
    /// dictionary behind the headers is bounded because it holds one entry per source address and
    /// is reachable through an anonymous POST; what must never follow from that bound is a caller
    /// who gets in free because there was no room left to count it in.
    /// </summary>
    [Fact]
    public async Task CreateBooking_FromACallerPastTheRememberedPartitions_IsLimitedWithoutHeaders()
    {
        using var app = new KairosApp();
        var host = RememberingOneCaller(app, RateLimitOptions.DefaultWindow);

        using var remembered = ForwardedClient(host, FirstCaller);
        using var forgotten = ForwardedClient(host, SecondCaller);

        // The one slot there is goes to whoever arrives first.
        Has(await AttemptAsync(remembered), RateLimitPolicies.RemainingHeader).ShouldBeTrue();

        for (var attempt = 0; attempt < WritePermits; attempt++)
        {
            var admitted = await AttemptAsync(forgotten);

            admitted.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            Has(admitted, RateLimitPolicies.RemainingHeader).ShouldBeFalse();
        }

        (await AttemptAsync(forgotten)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    /// <summary>A host that remembers one caller, over a window of the given length.</summary>
    private static WebApplicationFactory<KairosDbContext> RememberingOneCaller(
        KairosApp app,
        TimeSpan window) =>
        ShippingLimitHost(app).WithWebHostBuilder(builder =>
        {
            builder.UseSetting(MaxPartitionsKey, Text(RememberOne));
            builder.UseSetting(WindowKey, window.ToString());
            builder.ConfigureServices(
                services => services.AddSingleton<IStartupFilter, LoopbackPeer>());
        });

    /// <summary>A host at the shipping limits rather than the ones the harness raises.</summary>
    private static WebApplicationFactory<KairosDbContext> ShippingLimitHost(KairosApp app) =>
        app.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(WritePermitsKey, Text(WritePermits));
            builder.UseSetting(ReadPermitsKey, Text(ReadPermits));
        });

    private static HttpClient ShippingLimit(KairosApp app) => ShippingLimitHost(app).CreateClient();

    /// <summary>
    /// A client of the given host, arriving as <paramref name="caller"/> through the proxy that
    /// <see cref="LoopbackPeer"/> stands in for.
    /// </summary>
    private static HttpClient ForwardedClient(
        WebApplicationFactory<KairosDbContext> host,
        string caller)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(ForwardedHeadersDefaults.XForwardedForHeaderName, caller);
        return client;
    }

    /// <summary>
    /// The request bodies are deliberately unresolvable — a 404 costs a lookup and no mail, and
    /// the limiter runs before the handler either way. What is being counted is requests, not
    /// bookings, which is the whole point: a rejected attempt still costs a full slot computation.
    /// </summary>
    private static Task<HttpResponseMessage> AttemptAsync(HttpClient client) =>
        client.PostAsJsonAsync(
            $"{ApiVersions.V1}/bookings",
            new
            {
                slug = UnknownSlug,
                start = Start,
                durationMinutes = DurationMinutes,
                name = BookerName,
                email = BookerEmail,
                timeZone = BookerZone,
            },
            TestContext.Current.CancellationToken);

    private static bool Has(HttpResponseMessage response, string name) =>
        response.Headers.Contains(name);

    private static int HeaderOf(HttpResponseMessage response, string name) =>
        int.Parse(response.Headers.GetValues(name).Single(), CultureInfo.InvariantCulture);

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The peer the in-memory host does not have. TestServer opens no socket, so
    /// <c>Connection.RemoteIpAddress</c> is null, and <c>UseForwardedHeaders</c> will not take a
    /// header's word for who is calling unless the connection itself came from an address it
    /// trusts — with nothing to check, it does nothing, and every client shares one partition.
    /// Loopback is what the framework trusts by default, so lending the connection that address is
    /// the whole substitution: the shipping middleware then resolves the caller for real.
    /// </summary>
    /// <remarks>
    /// An <see cref="IStartupFilter"/> rather than test middleware, because this has to run before
    /// <c>UseForwardedHeaders</c> — which is the first line of the application's own pipeline, and
    /// nothing registered through <c>Configure</c> can get in front of it.
    /// </remarks>
    private sealed class LoopbackPeer : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, forward) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.IPv6Loopback;
                await forward(context);
            });

            next(app);
        };
    }
}
