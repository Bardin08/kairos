using System.Globalization;

using Kairos.Api.Shared;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Kairos.Api.Security;

/// <summary>
/// The rate limit as the wire sees it: what every response tells a caller about where it stands,
/// and what a refusal looks like. The budget is declared at each mapping site as endpoint metadata,
/// so "which routes are limited?" is answered by reading the mapping rather than this file. The
/// anonymous routes that declare none are the six outside the admin prefix, and
/// <see cref="Shared.AdminGroup"/>'s remarks record why.
/// </summary>
public static class RateLimitPolicies
{
    public const string LimitHeader = "RateLimit-Limit";
    public const string RemainingHeader = "RateLimit-Remaining";
    public const string ResetHeader = "RateLimit-Reset";

    /// <summary>The code a client keys on, in the shape DomainExceptionHandler writes.</summary>
    public const string TooManyRequestsCode = "too_many_requests";

    private const string TooManyRequests =
        "Too many requests. Wait for the window named in the headers and try again.";

    /// <summary>
    /// The partition of a caller Kestrel can name no address for: every request through the
    /// in-memory test host, and in production a listener on a Unix domain socket or a named pipe —
    /// nginx in front of Kestrel over a UDS is an ordinary self-hosted shape. There every caller
    /// falls into this single partition, so the whole site shares one budget, and
    /// <c>UseForwardedHeaders</c> declines to read <c>X-Forwarded-For</c> as well, because a peer
    /// that does not exist is in nobody's <c>KnownProxies</c>. Hence
    /// <see cref="RateLimitBudget.WarnAboutMissingPeerOnce"/>: the failure is total and otherwise
    /// silent.
    /// </summary>
    private const string UnknownCaller = "unknown";

    private const int WholeSecond = 1;

    public static IServiceCollection AddRateLimitPolicies(this IServiceCollection services)
    {
        // Validated at start, like every other configuration this application cannot run without:
        // a permit count or a window at zero would refuse every anonymous caller forever, silently
        // — an outage wearing a limiter's clothes.
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
            .ValidateOnStart();

        services.AddSingleton<RateLimitBudget>();

        return services;
    }

    /// <summary>
    /// Declares which budget this endpoint spends from. The class rides along as metadata rather
    /// than being looked up by route inside the middleware, so an endpoint added later is limited
    /// by saying so beside it and never by editing a list somewhere else.
    /// </summary>
    public static TBuilder RateLimited<TBuilder>(this TBuilder builder, RateLimitClass limit)
        where TBuilder : IEndpointConventionBuilder => builder.WithMetadata(limit);

    /// <summary>
    /// Runs after routing, because the endpoint is what says whether there is a budget to spend.
    /// A request whose endpoint declares none passes through untouched and carries no headers.
    /// </summary>
    public static IApplicationBuilder UseRateLimiting(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<RateLimitClass>() is not { } limit)
            {
                await next(context);
                return;
            }

            var options = context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
            var budget = context.RequestServices.GetRequiredService<RateLimitBudget>();

            // The address is the forwarded one: UseForwardedHeaders runs first, so behind a proxy
            // this is the caller rather than the proxy. Without that line every caller on earth
            // would share one partition and throttle each other.
            var address = context.Connection.RemoteIpAddress;
            if (address is null)
            {
                budget.WarnAboutMissingPeerOnce();
            }

            var reading = budget.Spend(
                $"{limit.Name}:{address?.ToString() ?? UnknownCaller}",
                limit.Permits(options),
                options.Window);

            Publish(context, reading);

            if (reading.Allowed)
            {
                await next(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = Text(Seconds(reading.Reset));

            // The same shape DomainExceptionHandler writes, so a client parses one thing. Written
            // here rather than thrown, because the refusal happens before any handler runs.
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
        });

    private static void Publish(HttpContext context, RateLimitReading reading)
    {
        // Registered on the response rather than written now: UseExceptionHandler clears the
        // response before re-executing the pipeline, and every DomainException here goes through
        // that. Headers written eagerly would survive an ordinary 200 and vanish from exactly the
        // 404, 409 and 400 a client hammering this API most needs them on.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[LimitHeader] = Text(reading.Limit);
            context.Response.Headers[RemainingHeader] = Text(reading.Remaining);
            context.Response.Headers[ResetHeader] = Text(Seconds(reading.Reset));
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Rounded up, and never below a second. This is the remainder of a window, so truncating it
    /// answers zero for any refusal in the window's final second — telling the caller the limiter
    /// has just decided is asking too often to come straight back, which is the storm the limit
    /// exists to stop.
    /// </summary>
    private static int Seconds(TimeSpan value) =>
        Math.Max(WholeSecond, (int)Math.Ceiling(value.TotalSeconds));

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
