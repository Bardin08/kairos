namespace Kairos.Api.Security;

/// <summary>
/// The headers every response carries, whatever it is. A middleware rather than endpoint metadata,
/// because a header set that some responses miss is the same defect as no header set — and the
/// responses most likely to be forgotten are the static assets, which have no endpoint to hang
/// metadata on.
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
    /// The whole URL, withheld from everybody. The weaker <c>strict-origin-when-cross-origin</c>
    /// still sends the full URL on a SAME-origin request, so every fetch the manage page makes
    /// would carry <c>Referer: https://host/manage/&lt;token&gt;</c> into the front proxy — and the
    /// combined log format records that field. The manage token is a capability, and a capability
    /// does not belong in an access log.
    /// </summary>
    public const string NoReferrer = "no-referrer";

    /// <summary>
    /// Kairos asks for none of the three, so the page is refused them rather than merely never
    /// calling for them. Named individually because an empty allowlist is per-feature; there is no
    /// spelling of "deny everything".
    /// </summary>
    public const string NoDeviceAccess = "camera=(), microphone=(), geolocation=()";

    /// <summary>
    /// Registered first, ahead of the static files middleware and of the exception handler. Both
    /// answer without ever reaching an endpoint — one short-circuits on a file that exists, the
    /// other re-executes a cleared response — and a set that misses either of them is a set the
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

        return app.Use(async (context, next) =>
        {
            // Registered on the response rather than written now, and measured rather than
            // assumed: written eagerly, the whole set survives an ordinary 200 and is missing from
            // every response UseExceptionHandler writes — which is every DomainException in this
            // application. That handler clears the response and re-executes the pipeline from
            // BELOW this middleware, so nothing puts the headers back. A callback survives the
            // clear, because it lives on the response feature rather than in the headers.
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[ContentSecurityPolicyHeader] = policy;
                context.Response.Headers[FrameOptionsHeader] = NoFraming;
                context.Response.Headers[ContentTypeOptionsHeader] = NoSniffing;
                context.Response.Headers[ReferrerPolicyHeader] = NoReferrer;
                context.Response.Headers[PermissionsPolicyHeader] = NoDeviceAccess;
                return Task.CompletedTask;
            });

            await next(context);
        });
    }
}
