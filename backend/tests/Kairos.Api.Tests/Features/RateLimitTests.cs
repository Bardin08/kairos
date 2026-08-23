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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The anonymous write path, throttled. POST /bookings is what an unlimited caller turns into an
/// open relay: every accepted request mails a caller-supplied address with caller-supplied prose,
/// from the organizer's relay, under their domain's reputation, and burns their calendar quota.
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

    private const string WindowKey =
        $"{RateLimitOptions.SectionName}:{nameof(RateLimitOptions.Window)}";

    private const string UnknownSlug = "no-such-link";

    /// <summary>Parsed rather than formatted, so nothing about the instant depends on the clock.</summary>
    private const string Start = "2026-09-07T09:00:00Z";

    private const string Day = "2026-09-07";

    private const int DurationMinutes = 30;
    private const string BookerName = "Vlad";
    private const string BookerEmail = "vlad@example.com";
    private const string BookerZone = "Europe/Warsaw";

    /// <summary>Two callers arriving through the same proxy, from the documentation ranges.</summary>
    private const string FirstCaller = "203.0.113.9";

    private const string SecondCaller = "198.51.100.4";

    /// <summary>A budget of one, so the request after the first is refused with the window running.</summary>
    private const int SinglePermit = 1;

    private const int NoneLeft = 0;

    /// <summary>
    /// Zero, written as configuration is written. Every one of the three settings is unusable at
    /// this value, which is what the startup validation exists to say out loud.
    /// </summary>
    private const string Unusable = "0";

    /// <summary>The smallest honest Retry-After, and what a truncating cast answers as zero.</summary>
    private const int WholeSecond = 1;

    /// <summary>How many times a warning about a deployment shape is worth saying.</summary>
    private const int Once = 1;

    /// <summary>
    /// One second, so every refusal against it falls inside the window's last second — the case
    /// where rounding the remainder down and rounding it up give different answers.
    /// </summary>
    private static readonly TimeSpan BriefWindow = TimeSpan.FromSeconds(WholeSecond);

    /// <summary>
    /// The window every test here runs against, and the one thing that has to stay long enough for
    /// the read test's sixty requests. Named once so the host that configures it and the assertion
    /// that checks a reset against it cannot drift apart.
    /// </summary>
    private static readonly TimeSpan Window = RateLimitOptions.DefaultWindow;

    [Fact]
    public async Task CreateBooking_PastTheWindowsPermits_IsRefusedWith429RetryAfterAndHeaders()
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

        // The refusal states the budget as well, and OnRejected is the only place that can: the
        // middleware publishing them on the accepted path never runs for a request turned away.
        HeaderOf(refused, RateLimitPolicies.LimitHeader).ShouldBe(WritePermits);
        HeaderOf(refused, RateLimitPolicies.RemainingHeader).ShouldBe(NoneLeft);

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
            .ShouldBeInRange(WholeSecond, (int)Window.TotalSeconds);

        var second = await AttemptAsync(client);
        spent++;

        HeaderOf(second, RateLimitPolicies.RemainingHeader).ShouldBe(WritePermits - spent);
    }

    /// <summary>
    /// A refusal inside the window's last second still asks for a whole one. The metadata is the
    /// window's remainder, so truncating it answers <c>Retry-After: 0</c> to the caller just
    /// judged to be asking too often. Against a one-second window every refusal is that case.
    /// </summary>
    /// <remarks>
    /// A flake here is the two requests straddling a window boundary, not the rounding: past it
    /// the second is admitted and the failure reads "expected 429". Widen the window and keep the
    /// assertion — the exact <c>Retry-After</c> of one whole window is what dies if the ceiling
    /// becomes a truncating cast again.
    /// </remarks>
    [Fact]
    public async Task CreateBooking_RefusedInsideTheWindowsLastSecond_RoundsRetryAfterUpNotToZero()
    {
        using var app = new KairosApp();
        var host = ShippingLimitHost(app).WithWebHostBuilder(builder =>
        {
            builder.UseSetting(WritePermitsKey, Text(SinglePermit));
            builder.UseSetting(WindowKey, BriefWindow.ToString());
        });

        // Warmed up on another caller's partition, so that host startup sits outside the pair of
        // requests below — they have to land within one second of each other to prove anything.
        using var warmUp = ForwardedClient(host, FirstCaller);
        await AttemptAsync(warmUp);

        using var client = ForwardedClient(host, SecondCaller);
        await AttemptAsync(client);

        var refused = await AttemptAsync(client);

        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        // Asserted apart rather than through a null-conditional, which would quietly skip the
        // comparison on the absent header this is here to notice.
        var retryAfter = refused.Headers.RetryAfter;
        retryAfter.ShouldNotBeNull();
        retryAfter.Delta.ShouldBe(BriefWindow);

        HeaderOf(refused, RateLimitPolicies.ResetHeader).ShouldBe(WholeSecond);
    }

    /// <summary>
    /// The read class, on this API's cheap denial-of-service target: one call materializes a
    /// month of intervals and nothing is written. Its budget is the larger one, so this also pins
    /// that the two classes are two numbers.
    /// </summary>
    /// <remarks>
    /// Seen red once under parallel load with the limiter the wrong suspect: the cause was the
    /// process-global <c>SqliteConnection.ClearAllPools()</c> in the harness teardown disposing
    /// this test's pooled connections mid-loop. The tell is which assertion fails — inside the
    /// loop is infrastructure, the last one is the limiter. <see cref="ShippingLimitHost"/> pins
    /// the window so all sixty-one requests land inside one.
    /// </remarks>
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
        var host = ShippingLimitHost(app);
        using var exhausted = ForwardedClient(host, FirstCaller);
        using var arriving = ForwardedClient(host, SecondCaller);

        for (var attempt = 0; attempt < WritePermits; attempt++)
        {
            await AttemptAsync(exhausted);
        }

        (await AttemptAsync(exhausted)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        var spent = 0;
        var first = await AttemptAsync(arriving);
        spent++;

        // Admitted, and against a budget nothing has spent from but this one request.
        first.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        HeaderOf(first, RateLimitPolicies.RemainingHeader).ShouldBe(WritePermits - spent);
    }

    /// <summary>
    /// A limit that cannot work stops the process rather than being discovered later. Zero
    /// permits or a zero window throw from the limiter's constructor — a 500 on every anonymous
    /// request for the life of the deployment — while zero remembered partitions throws nothing
    /// and silently turns every <c>RateLimit-*</c> header off. The third is why this is validated.
    /// </summary>
    [Theory]
    [InlineData(WritePermitsKey)]
    [InlineData(ReadPermitsKey)]
    [InlineData(WindowKey)]
    public void CreateClient_WithAnUnusableRateLimitSetting_FailsAtStartup(string key)
    {
        using var app = new KairosApp();
        var host = ShippingLimitHost(app).WithWebHostBuilder(
            builder => builder.UseSetting(key, Unusable));

        Should.Throw<OptionsValidationException>(() => host.CreateClient());
    }

    /// <summary>
    /// A listener with no peer collapses every caller into one partition and makes
    /// <c>X-Forwarded-For</c> untrustworthy, so the whole site shares one budget. No response
    /// says so, hence the log — said once, because a line per request is a second denial of
    /// service on a host already being flooded.
    /// </summary>
    /// <remarks>
    /// The bare harness host is the one shape that reproduces this, TestServer opening no socket,
    /// so this test deliberately skips <see cref="ShippingLimitHost"/> and its lent peer.
    /// </remarks>
    [Fact]
    public async Task CreateBooking_FromAConnectionWithNoPeer_WarnsOnceAndNotOnEveryRequest()
    {
        using var app = new KairosApp();
        var warnings = new WarningCounter();

        using var client = app
            .WithWebHostBuilder(builder => builder.ConfigureServices(
                services => services.AddSingleton<ILoggerProvider>(warnings)))
            .CreateClient();

        await AttemptAsync(client);
        await AttemptAsync(client);

        warnings.Count.ShouldBe(Once);
    }

    /// <summary>
    /// A host at the shipping limits rather than the ones the harness raises, whose connections
    /// carry the peer <see cref="LoopbackPeer"/> lends them. Every test here runs on one, so there
    /// is a single place that knows how this harness stands in for a proxy.
    /// </summary>
    private static WebApplicationFactory<KairosDbContext> ShippingLimitHost(KairosApp app) =>
        app.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(WritePermitsKey, Text(WritePermits));
            builder.UseSetting(ReadPermitsKey, Text(ReadPermits));

            // Pinned rather than inherited. It equals the shipping default, but the read test
            // spends sixty permits one at a time inside a window anchored at the limiter's
            // construction, so changing that default must not silently shorten it.
            builder.UseSetting(WindowKey, Window.ToString());

            builder.ConfigureServices(
                services => services.AddSingleton<IStartupFilter, LoopbackPeer>());
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
    /// Counts warnings from one category. Filtered, because the harness's own startup says things
    /// — an unconfigured mail relay, for one — and counting those would pass whatever the code
    /// under test did.
    /// </summary>
    private sealed class WarningCounter : ILoggerProvider, ILogger
    {
        private int _count;

        public int Count => _count;

        public ILogger CreateLogger(string categoryName) =>
            categoryName == typeof(RateLimitBudget).FullName ? this : NullLogger.Instance;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Interlocked.Increment(ref _count);
            }
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// The peer the in-memory host does not have. TestServer opens no socket, so
    /// <c>Connection.RemoteIpAddress</c> is null and <c>UseForwardedHeaders</c> takes no header's
    /// word for who is calling, leaving every client in one partition. Loopback is what the
    /// framework trusts by default, so lending that address is the substitution.
    /// </summary>
    /// <remarks>
    /// An <see cref="IStartupFilter"/> rather than test middleware: nothing registered through
    /// <c>Configure</c> gets in front of <c>UseForwardedHeaders</c>.
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
