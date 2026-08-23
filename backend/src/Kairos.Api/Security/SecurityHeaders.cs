using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Kairos.Api.Security;

/// <summary>
/// The headers every response carries. A middleware rather than endpoint metadata, because a
/// header set some responses miss is the same defect as no header set — and the responses most
/// likely to be forgotten are the static assets, which have no endpoint to hang metadata on.
/// </summary>
public static class SecurityHeaders
{
    public const string ContentSecurityPolicyHeader = "Content-Security-Policy";

    public const string FrameOptionsHeader = "X-Frame-Options";

    public const string ContentTypeOptionsHeader = "X-Content-Type-Options";

    public const string ReferrerPolicyHeader = "Referrer-Policy";

    public const string PermissionsPolicyHeader = "Permissions-Policy";

    /// <summary>
    /// Redundant beside <c>frame-ancestors</c> for every browser that reads a CSP, and one line
    /// for everything that does not. The admin surface deletes a link in one click, which is
    /// exactly what an invisible frame over a decoy page is for.
    /// </summary>
    public const string NoFraming = "DENY";

    public const string NoSniffing = "nosniff";

    /// <summary>
    /// The whole URL, withheld from everybody. It does not keep the manage token out of a log —
    /// the origin server already writes <c>GET /manage/&lt;token&gt;</c> as its request line. What
    /// it stops is the token travelling as <c>Referer</c> on every OTHER request the manage page
    /// makes, which is where the weaker <c>strict-origin-when-cross-origin</c> leaks: it still
    /// sends the full URL same-origin, so each fetch would carry the token into a log line of its
    /// own, and any outbound link would carry the origin off this host entirely.
    /// </summary>
    public const string NoReferrer = "no-referrer";

    /// <summary>
    /// Kairos asks for none of the three, so the page is refused them rather than merely never
    /// calling for them. Named individually because an empty allowlist is per-feature; there is no
    /// spelling of "deny everything".
    /// </summary>
    public const string NoDeviceAccess = "camera=(), microphone=(), geolocation=()";

    private const string MaxAge = "max-age=";

    private const string IncludeSubDomains = "; includeSubDomains";

    private const string Preload = "; preload";

    /// <summary>
    /// Registered first, ahead of the static files middleware and the exception handler. Both
    /// answer without ever reaching an endpoint — one short-circuits on a file that exists, the
    /// other clears the response and writes its own — and a set that misses either is a set the
    /// single page and every refusal go out without.
    /// </summary>
    /// <remarks>
    /// The policy is built once, here, while the pipeline is being assembled. It reads the page on
    /// disk, and neither the page nor the answer changes for the life of the process.
    /// </remarks>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        var policy = ContentSecurityPolicy.For(
            app.ApplicationServices.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider);

        // The values AddHsts was given. Read from the framework's own options rather than from
        // configuration a second time, so AddHsts stays the single place the age and the two flags
        // are decided — and so does the excluded-hosts list this consults below.
        var hsts = app.ApplicationServices.GetRequiredService<IOptions<HstsOptions>>().Value;
        var transportSecurity = TransportSecurity(hsts);

        return app.Use(async (context, next) =>
        {
            // Registered on the response rather than written now, measured rather than assumed:
            // written eagerly, the set survives an ordinary 200 and is missing from every response
            // UseExceptionHandler writes — which is every DomainException here. That handler clears
            // the response and hands it to the registered IExceptionHandler; with no re-execution
            // path configured it never runs the pipeline again, so no middleware can put an eagerly
            // written header back. A callback survives the clear, because it lives on the response
            // feature rather than in the headers, and runs when the handler's response starts.
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[ContentSecurityPolicyHeader] = policy;
                context.Response.Headers[FrameOptionsHeader] = NoFraming;
                context.Response.Headers[ContentTypeOptionsHeader] = NoSniffing;
                context.Response.Headers[ReferrerPolicyHeader] = NoReferrer;
                context.Response.Headers[PermissionsPolicyHeader] = NoDeviceAccess;

                if (IsSecure(context.Request, hsts))
                {
                    context.Response.Headers[HeaderNames.StrictTransportSecurity] = transportSecurity;
                }

                return Task.CompletedTask;
            });

            await next(context);
        });
    }

    /// <summary>
    /// The gate <c>UseHsts</c> applies, applied here instead. That middleware writes its header
    /// eagerly, so on the exception path it is cleared and never rewritten — measured: a 404 from
    /// a DomainException went out over HTTPS with no HSTS at all, while a 404 from an unmapped
    /// route carried it. Moving it below <c>UseExceptionHandler</c> does not help, because that
    /// handler re-runs no middleware. So Kairos does not call <c>UseHsts</c> and writes the header
    /// from the callback above, which is the only place in this pipeline that outlives the clear.
    /// </summary>
    /// <remarks>
    /// The scheme is the forwarded one — <c>UseForwardedHeaders</c> runs first — so behind a
    /// TLS-terminating proxy this is what the caller really used. The excluded hosts are the
    /// framework's own defaults, loopback among them: promising a browser that <c>localhost</c> is
    /// HTTPS-only would break every other application a developer runs on it.
    /// </remarks>
    private static bool IsSecure(HttpRequest request, HstsOptions hsts) =>
        request.IsHttps
        && !hsts.ExcludedHosts.Contains(request.Host.Host, StringComparer.OrdinalIgnoreCase);

    private static string TransportSecurity(HstsOptions hsts)
    {
        var value = $"{MaxAge}{(long)hsts.MaxAge.TotalSeconds}";
        if (hsts.IncludeSubDomains)
        {
            value += IncludeSubDomains;
        }

        return hsts.Preload ? value + Preload : value;
    }
}
