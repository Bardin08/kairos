using System.Globalization;
using System.Threading.RateLimiting;

using Kairos.Api.Shared;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Kairos.Api.Security;

/// <summary>
/// Two policies, one per class of anonymous endpoint, and the headers that tell a caller where it
/// stands. Named policies rather than a global limiter, because every anonymous endpoint states
/// its limit at the mapping site — including the ones whose answer is "none".
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>A request that writes, mails, or reaches a provider on the caller's behalf.</summary>
    public const string AnonymousWrite = "anonymous-write";

    /// <summary>A request that only reads, but computes a month of intervals to do it.</summary>
    public const string AnonymousRead = "anonymous-read";

    public const string LimitHeader = "RateLimit-Limit";
    public const string RemainingHeader = "RateLimit-Remaining";
    public const string ResetHeader = "RateLimit-Reset";

    /// <summary>The code a client keys on, in the shape DomainExceptionHandler writes.</summary>
    public const string TooManyRequestsCode = "too_many_requests";

    private const string TooManyRequests =
        "Too many requests. Wait for the window named in the headers and try again.";

    /// <summary>
    /// The partition of a caller with no address — every request through the in-memory test host,
    /// and nothing in production, where Kestrel always has a peer.
    /// </summary>
    private const string UnknownCaller = "unknown";

    private const string PartitionItemKey = "kairos.rate-limit.partition";

    public static IServiceCollection AddKairosRateLimiter(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RateLimitOptions>(configuration.GetSection(RateLimitOptions.SectionName));
        services.AddSingleton<RateLimitBudget>();

        services.AddRateLimiter(limiter =>
        {
            limiter.AddPolicy(AnonymousWrite, context => Partition(
                context, AnonymousWrite, options => options.WritePermits));
            limiter.AddPolicy(AnonymousRead, context => Partition(
                context, AnonymousRead, options => options.ReadPermits));

            limiter.OnRejected = async (rejected, cancellationToken) =>
            {
                var context = rejected.HttpContext;
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;

                if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                Publish(context);

                // The same shape DomainExceptionHandler writes, so a client parses one thing.
                // Written here rather than thrown, because a refusal happens before any handler.
                await context.RequestServices.GetRequiredService<IProblemDetailsService>()
                    .TryWriteAsync(new ProblemDetailsContext
                    {
                        HttpContext = context,
                        ProblemDetails = new ProblemDetails
                        {
                            Status = StatusCodes.Status429TooManyRequests,
                            Title = TooManyRequests,
                            Extensions = { [ProblemCode.Key] = TooManyRequestsCode },
                        },
                    });
            };
        });

        return services;
    }

    /// <summary>
    /// Runs after <c>UseRateLimiter</c>, so it is only reached by a request the limiter admitted.
    /// The refused ones get their headers from <c>OnRejected</c>, which is the only place that
    /// still has the response.
    /// </summary>
    public static IApplicationBuilder UseRateLimitHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            Publish(context);
            await next(context);
        });

    private static RateLimitPartition<string> Partition(
        HttpContext context,
        string policy,
        Func<RateLimitOptions, int> permits)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
        var budget = context.RequestServices.GetRequiredService<RateLimitBudget>();

        // The address is the forwarded one: UseForwardedHeaders runs first, so behind a proxy
        // this is the caller rather than the proxy. Without that line every caller on earth would
        // share one partition and throttle each other.
        var caller = context.Connection.RemoteIpAddress?.ToString() ?? UnknownCaller;
        var partition = $"{policy}:{caller}";

        // Stashed rather than recomputed, so the headers publish the partition that was scored.
        context.Items[PartitionItemKey] = partition;

        return RateLimitPartition.Get(
            partition,
            key => budget.Limiter(key, permits(options), options.Window));
    }

    private static void Publish(HttpContext context)
    {
        if (context.Items[PartitionItemKey] is not string partition)
        {
            return;
        }

        var reading = context.RequestServices.GetRequiredService<RateLimitBudget>().Read(partition);
        if (reading is not { } budget)
        {
            return;
        }

        // On the response rather than written now: the handler has not run yet on the accepted
        // path, and a header set before the status code is chosen is a header the framework drops.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[LimitHeader] = Text(budget.Limit);
            context.Response.Headers[RemainingHeader] = Text(budget.Remaining);
            context.Response.Headers[ResetHeader] = Text((int)budget.Reset.TotalSeconds);
            return Task.CompletedTask;
        });
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
