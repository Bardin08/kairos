using System.Globalization;
using System.Threading.RateLimiting;

using Kairos.Api.Shared;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Kairos.Api.Security;

/// <summary>
/// Two policies, one per class of anonymous endpoint, and the headers that tell a caller where it
/// stands. Named policies rather than a global limiter, so a budget is declared where the endpoint
/// carrying it is mapped. The anonymous routes that declare none are the four outside the admin
/// prefix, and <see cref="Shared.AdminGroup"/>'s remarks are where that decision is recorded.
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
    /// The partition of a caller Kestrel can name no address for. That is every request through
    /// the in-memory test host, and in production it is a listener on a Unix domain socket or a
    /// named pipe — nginx in front of Kestrel over a UDS is an ordinary self-hosted shape. There
    /// every caller falls into this single partition, so the whole site shares one budget, and
    /// <c>UseForwardedHeaders</c> declines to read <c>X-Forwarded-For</c> as well, because a peer
    /// that does not exist is in nobody's <c>KnownProxies</c>. Hence
    /// <see cref="RateLimitBudget.WarnAboutMissingPeerOnce"/>: the failure is total, and otherwise
    /// completely silent.
    /// </summary>
    private const string UnknownCaller = "unknown";

    private const string PartitionItemKey = "kairos.rate-limit.partition";

    public static IServiceCollection AddRateLimitPolicies(this IServiceCollection services)
    {
        // Validated, and validated at start: every other configuration this application cannot run
        // without stops the process rather than each request. MaxPartitions at zero would turn the
        // headers off everywhere and say nothing; a permit count or a window at zero throws from
        // the limiter's own constructor, which without this is a 500 on every request forever
        // instead of one line at boot.
        services.AddOptions<RateLimitOptions>()
            .BindConfiguration(RateLimitOptions.SectionName)
            .Validate(
                options => options.WritePermits > 0 && options.ReadPermits > 0,
                $"{RateLimitOptions.SectionName}: WritePermits and ReadPermits must be at least 1. "
                + "Zero would refuse every anonymous request, which is not a rate limit but an "
                + "outage.")
            .Validate(
                options => options.Window > TimeSpan.Zero,
                $"{RateLimitOptions.SectionName}: Window must be longer than zero — it is the "
                + "period the permits above are counted over.")
            .Validate(
                options => options.MaxPartitions > 0,
                $"{RateLimitOptions.SectionName}: MaxPartitions must be at least 1. Zero remembers "
                + "no caller, so every response would silently lose its RateLimit-* headers.")
            .ValidateOnStart();

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
                    context.Response.Headers.RetryAfter = Text(Seconds(retryAfter));
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
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            budget.WarnAboutMissingPeerOnce();
        }

        var partition = $"{policy}:{address?.ToString() ?? UnknownCaller}";

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

        // Registered on the response rather than written now, for two reasons and not the one it
        // looks like: on the accepted path the handler has not run yet, and — the reason that
        // decides it — UseExceptionHandler clears the response before re-executing the pipeline.
        // Every DomainException in this application goes through that, so eagerly written headers
        // would survive an ordinary 200 and vanish from exactly the 404, 409 and 400 that a client
        // hammering this API most needs them on.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[LimitHeader] = Text(budget.Limit);
            context.Response.Headers[RemainingHeader] = Text(budget.Remaining);
            context.Response.Headers[ResetHeader] = Text(Seconds(budget.Reset));
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Rounded up, never truncated. These are both remainders of a window, so truncation answers
    /// zero for any refusal in its final second — telling the caller the limiter has just decided
    /// is asking too often to come straight back, which is the storm this exists to stop.
    /// </summary>
    private static int Seconds(TimeSpan value) => (int)Math.Ceiling(value.TotalSeconds);

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
