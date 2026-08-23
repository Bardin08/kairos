using System.Net;
using System.Security.Cryptography;
using System.Text;

using Kairos.Api.Security;
using Kairos.Api.Shared;

using Microsoft.Net.Http.Headers;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// What every response says about itself. The admin surface and the public booking page share an
/// origin with the API, so the two questions are whether the set reaches every kind of response
/// and whether the policy still describes the page on disk.
/// </summary>
/// <remarks>
/// The policy is asserted WHOLE rather than by whichever directive is interesting. It is one
/// string built in one place, and a containment check leaves every unnamed directive deletable
/// with the suite green.
/// </remarks>
public class SecurityHeadersTests
{
    /// <summary>A file on disk, answered by the static files middleware and by no endpoint.</summary>
    private const string StaticAssetPath = $"/{ContentSecurityPolicy.IndexPage}";

    /// <summary>The cheapest anonymous API route: what is under test is the pipeline, not sign-in.</summary>
    private const string ApiPath = $"{ApiVersions.V1}/sign-in";

    /// <summary>A slug nothing seeds, so the request ends in the exception handler's 404.</summary>
    private const string MissingLinkPath = $"{ApiVersions.V1}/links/no-such-link";

    /// <summary>
    /// Not localhost. The excluded-hosts list carries the loopback names, and the harness's own
    /// base address is one of them — a test host answering as localhost would never see HSTS
    /// whatever the scheme said.
    /// </summary>
    private const string ProxiedOrigin = "http://kairos.test";

    /// <summary>And the host that IS on that list, for the test that says so.</summary>
    private const string ExcludedOrigin = "http://localhost";

    private const string ForwardedProtoHeader = "X-Forwarded-Proto";

    private const string Https = "https";

    private const string Http = "http";

    /// <summary>The shipped default, restated so that changing it has to be deliberate.</summary>
    private const int DefaultMaxAgeDays = 30;

    private const string ScriptIndent = "      ";

    private const string ThemeScript =
        "try { document.documentElement.setAttribute('data-theme', 'dark') } catch (e) {}";

    /// <summary>
    /// Modelled on the page this application ships: the executed text carries the newline and
    /// indentation of the element around it. That whitespace is between the tags, so a browser
    /// hashes it, and a policy built over a trimmed script matches nothing and refuses silently.
    /// </summary>
    private const string InlineScript = $"\n{ScriptIndent}{ThemeScript}\n{ScriptIndent}";

    private const string MarkedScriptBody = "queueMicrotask(() => {})";

    private const string MarkedScript = $"\n{ScriptIndent}{MarkedScriptBody}\n{ScriptIndent}";

    /// <summary>
    /// An attribute whose name ENDS in src and is not src. A match on the bare letters skips this
    /// element and its script executes nowhere — the hyphen is a non-word character, so a word
    /// boundary cannot tell the two apart. A valid policy that refuses a script.
    /// </summary>
    private const string MarkedScriptOpen = "<script data-src=\"deferred\">";

    /// <summary>The bundle, carrying a real <c>src</c> — nothing to hash, and nothing that may be.</summary>
    private const string BundlePath = "/assets/index.js";

    private const string ScriptOpen = "<script>";

    private const string ScriptClose = "</script>";

    private const string SinglePage =
        $"<!doctype html><html><head>{ScriptOpen}{InlineScript}{ScriptClose}"
        + $"{MarkedScriptOpen}{MarkedScript}{ScriptClose}"
        + $"<script type=\"module\" src=\"{BundlePath}\">{ScriptClose}</head><body></body></html>";

    /// <summary>What a web root with no page in it allows: the bundle, and nothing inline.</summary>
    private const string NoInlineScript = "'self'";

    private static readonly string ExpectedMaxAge =
        $"max-age={(int)TimeSpan.FromDays(DefaultMaxAgeDays).TotalSeconds}";

    /// <summary>
    /// Both kinds of response, because the middleware covers one and not the other when it is
    /// registered in the wrong place: the static files middleware short-circuits, so anything
    /// behind it never sees a request for a file that exists.
    /// </summary>
    [Theory]
    [InlineData(StaticAssetPath)]
    [InlineData(ApiPath)]
    public async Task Get_AStaticAssetOrAnApiRoute_CarriesTheWholeHeaderSet(string path) =>
        await WithSinglePageAsync(async app =>
        {
            var response = await app.Client.GetAsync(path, TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            ShouldCarryTheSet(response, ExpectedPolicy(AllowedScripts));
        });

    /// <summary>
    /// The test that stops the header and the page drifting apart. Both hashes are recomputed
    /// from the bytes the server returned, so a policy pinned to a stale constant fails the
    /// moment either script is edited. The second is the <c>data-src</c> one a looser match drops.
    /// </summary>
    [Fact]
    public async Task ContentSecurityPolicy_WithInlineScriptsOnThePage_HashesExactlyWhatIsServed() =>
        await WithSinglePageAsync(async app =>
        {
            var response = await app.Client.GetAsync(
                StaticAssetPath,
                TestContext.Current.CancellationToken);
            var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            var served = $"{NoInlineScript} {Hash(Between(page, ScriptOpen))} "
                + $"{Hash(Between(page, MarkedScriptOpen))}";

            Policy(response).ShouldBe(ExpectedPolicy(served));
        });

    /// <summary>
    /// HSTS is a promise about a scheme, so it is only honest on a request that arrived over that
    /// scheme. Behind a TLS-terminating proxy the only thing that knows is X-Forwarded-Proto,
    /// which is why the header is written behind UseForwardedHeaders.
    /// </summary>
    [Theory]
    [InlineData(Https, true)]
    [InlineData(Http, false)]
    public async Task StrictTransportSecurity_ByForwardedScheme_IsSentOnlyOverHttps(
        string scheme,
        bool expected)
    {
        using var app = new KairosApp();

        var response = await SendAsync(app, ProxiedOrigin, ApiPath, scheme);

        response.Headers.Contains(HeaderNames.StrictTransportSecurity).ShouldBe(expected);
        if (expected)
        {
            // The exact value, so the max-age and the two flags Kairos declines to set — a
            // subdomain pin and a preload entry on a domain it does not own — are both pinned.
            Single(response, HeaderNames.StrictTransportSecurity).ShouldBe(ExpectedMaxAge);
        }
    }

    /// <summary>
    /// The gate that is no longer the framework's to apply, so it is this suite's to pin.
    /// Promising a browser that <c>localhost</c> is HTTPS-only outlives this process and breaks
    /// every other application a developer runs on it.
    /// </summary>
    [Fact]
    public async Task StrictTransportSecurity_OnAnExcludedHost_IsWithheld()
    {
        using var app = new KairosApp();

        var response = await SendAsync(app, ExcludedOrigin, ApiPath, Https);

        response.Headers.Contains(HeaderNames.StrictTransportSecurity).ShouldBeFalse();
    }

    /// <summary>
    /// The refusal a client actually meets, over HTTPS. Measured downstream through a real proxy:
    /// a 404 from an unmapped route carried HSTS and this one did not, because UseHsts writes its
    /// header eagerly and the exception handler clears the response without re-running anything.
    /// </summary>
    [Fact]
    public async Task StrictTransportSecurity_OnADomainExceptionResponse_IsStillSent()
    {
        using var app = new KairosApp();

        var response = await SendAsync(app, ProxiedOrigin, MissingLinkPath, Https);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        Single(response, HeaderNames.StrictTransportSecurity).ShouldBe(ExpectedMaxAge);
    }

    /// <summary>
    /// The response an operator is most likely to meet while something is already wrong. The
    /// exception handler clears the response and writes its own, so this is the response a header
    /// written on the way in does not reach.
    /// </summary>
    [Fact]
    public async Task Get_AMissingLink_CarriesTheWholeHeaderSetOnTheRefusal() =>
        await WithSinglePageAsync(async app =>
        {
            var response = await app.Client.GetAsync(
                MissingLinkPath,
                TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            ShouldCarryTheSet(response, ExpectedPolicy(AllowedScripts));
        });

    /// <summary>
    /// The backend-only host: no page, so nothing inline may run. Given an empty web root of its
    /// own rather than the default, which is the project's directory — the SPA build writes a page
    /// there, so a test reading it would assert a different policy per machine.
    /// </summary>
    [Fact]
    public async Task ContentSecurityPolicy_WithNoPageInTheWebRoot_AllowsNoInlineScript() =>
        await WithEmptyWebRootAsync(async app =>
        {
            var response = await app.Client.GetAsync(
                ApiPath,
                TestContext.Current.CancellationToken);

            ShouldCarryTheSet(response, ExpectedPolicy(NoInlineScript));
        });

    /// <summary>
    /// The policy in full. Every directive is named, so deleting any one of them fails here rather
    /// than passing unnoticed.
    /// </summary>
    private static string ExpectedPolicy(string scriptSources) =>
        "default-src 'none'; "
        + $"script-src {scriptSources}; "
        + "style-src 'self'; "
        + "font-src 'self'; "
        + "img-src 'self'; "
        + "connect-src 'self'; "
        + "form-action 'self'; "
        + "frame-ancestors 'none'; "
        + "base-uri 'none'; "
        + "object-src 'none'";

    /// <summary>Both inline scripts on the page above, in the order the document carries them.</summary>
    private static string AllowedScripts =>
        $"{NoInlineScript} {Hash(InlineScript)} {Hash(MarkedScript)}";

    private static void ShouldCarryTheSet(HttpResponseMessage response, string policy)
    {
        Policy(response).ShouldBe(policy);
        Single(response, SecurityHeaders.FrameOptionsHeader).ShouldBe(SecurityHeaders.NoFraming);
        Single(response, SecurityHeaders.ContentTypeOptionsHeader).ShouldBe(SecurityHeaders.NoSniffing);
        Single(response, SecurityHeaders.ReferrerPolicyHeader).ShouldBe(SecurityHeaders.NoReferrer);
        Single(response, SecurityHeaders.PermissionsPolicyHeader).ShouldBe(SecurityHeaders.NoDeviceAccess);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        KairosApp app,
        string origin,
        string path,
        string scheme)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{origin}{path}");
        request.Headers.Add(ForwardedProtoHeader, scheme);

        return await app.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>The text an element opened by <paramref name="open"/> executes, verbatim.</summary>
    private static string Between(string page, string open)
    {
        var start = page.IndexOf(open, StringComparison.Ordinal) + open.Length;
        var end = page.IndexOf(ScriptClose, start, StringComparison.Ordinal);

        return page[start..end];
    }

    private static string Policy(HttpResponseMessage response) =>
        Single(response, SecurityHeaders.ContentSecurityPolicyHeader);

    private static string Single(HttpResponseMessage response, string name) =>
        response.Headers.GetValues(name).Single();

    private static string Hash(string script) =>
        $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script)))}'";

    /// <summary>
    /// The application over a web root this test owns, cleaned up as <see cref="KairosApp"/>
    /// cleans its database. Never the default, which is the API project's own directory: its
    /// contents depend on whether anybody ran the SPA build in this clone.
    /// </summary>
    private static Task WithSinglePageAsync(Func<KairosApp, Task> assert) =>
        WithWebRootAsync(SinglePage, assert);

    private static Task WithEmptyWebRootAsync(Func<KairosApp, Task> assert) =>
        WithWebRootAsync(null, assert);

    private static async Task WithWebRootAsync(string? page, Func<KairosApp, Task> assert)
    {
        var webRoot = Directory.CreateTempSubdirectory("kairos-webroot");
        try
        {
            if (page is not null)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(webRoot.FullName, ContentSecurityPolicy.IndexPage),
                    page,
                    TestContext.Current.CancellationToken);
            }

            using var app = new KairosApp(webRoot.FullName);
            await assert(app);
        }
        finally
        {
            webRoot.Delete(recursive: true);
        }
    }
}
