using System.Net;
using System.Security.Cryptography;
using System.Text;

using Kairos.Api.Security;
using Kairos.Api.Shared;

using Microsoft.Net.Http.Headers;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// What every response says about itself. The admin surface and the public booking page are served
/// from the same origin as the API, and the workbench deletes a link in one click — so the two
/// questions here are whether the set reaches every kind of response, and whether the policy still
/// describes the page that is actually on disk.
/// </summary>
public class SecurityHeadersTests
{
    /// <summary>A file on disk, answered by the static files middleware and by no endpoint.</summary>
    private const string StaticAssetPath = $"/{ContentSecurityPolicy.IndexPage}";

    /// <summary>The cheapest anonymous API route: what is under test is the pipeline, not sign-in.</summary>
    private const string ApiPath = $"{ApiVersions.V1}/sign-in";

    /// <summary>A slug nothing seeds, so the request ends in the exception handler's 404.</summary>
    private const string MissingLinkPath = $"{ApiVersions.V1}/links/no-such-link";

    /// <summary>
    /// Not localhost. The HSTS middleware ships an excluded-hosts list with the loopback names on
    /// it, and the harness's own base address is one of them — a test host answering as localhost
    /// would never see the header whatever the scheme said.
    /// </summary>
    private const string ProxiedOrigin = "http://kairos.test";

    private const string ForwardedProtoHeader = "X-Forwarded-Proto";

    private const string Https = "https";

    private const string Http = "http";

    /// <summary>The shipped default, restated so that changing it has to be deliberate.</summary>
    private const int DefaultMaxAgeDays = 30;

    private const string ScriptIndent = "      ";

    private const string ScriptBody =
        "try { document.documentElement.setAttribute('data-theme', 'dark') } catch (e) {}";

    /// <summary>
    /// Modelled on the page this application ships: the executed text is wrapped in the newline
    /// and indentation of the element it sits inside. That whitespace is the point. It is between
    /// the tags, so a browser hashes it, and a policy built over a trimmed script produces a
    /// well-formed hash that matches nothing and silently refuses the script.
    /// </summary>
    private const string InlineScript = $"\n{ScriptIndent}{ScriptBody}\n{ScriptIndent}";

    /// <summary>The bundle, carrying a <c>src</c> — nothing to hash, and nothing that may be hashed.</summary>
    private const string BundlePath = "/assets/index.js";

    private const string ScriptOpen = "<script>";

    private const string ScriptClose = "</script>";

    private const string SinglePage =
        $"<!doctype html><html><head>{ScriptOpen}{InlineScript}{ScriptClose}"
        + $"<script type=\"module\" src=\"{BundlePath}\">{ScriptClose}</head><body></body></html>";

    private const string PolicySeparator = ";";

    private const string ScriptSourcePrefix = "script-src 'self' ";

    /// <summary>The directive the clickjacking finding turns on.</summary>
    private const string FramingRefused = "frame-ancestors 'none'";

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
    public async Task EveryResponse_CarriesTheHeaderSet(string path) =>
        await WithSinglePageAsync(async app =>
        {
            var response = await app.Client.GetAsync(path, TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            ShouldCarryTheSet(response);
        });

    /// <summary>
    /// The test that stops the header and the page drifting apart. The hash is recomputed here
    /// from the script the page really carries, so a policy pinned to a stale constant fails the
    /// moment somebody edits that script — which is the failure that is otherwise silent.
    /// </summary>
    [Fact]
    public async Task TheContentSecurityPolicy_HashesTheInlineScriptTheSinglePageCarries() =>
        await WithSinglePageAsync(async app =>
        {
            var response = await app.Client.GetAsync(
                StaticAssetPath,
                TestContext.Current.CancellationToken);
            var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            var start = page.IndexOf(ScriptOpen, StringComparison.Ordinal) + ScriptOpen.Length;
            var end = page.IndexOf(ScriptClose, StringComparison.Ordinal);
            var script = page[start..end];

            // The whole directive and not just the hash: an allowlist gains a source silently, and
            // 'unsafe-inline' beside a hash is exactly the mistake this policy exists to refuse.
            Policy(response).ShouldContain(
                $"{ScriptSourcePrefix}{Hash(script)}{PolicySeparator}",
                Case.Sensitive);
        });

    /// <summary>
    /// HSTS is a promise about a scheme, so it is only honest on a request that arrived over that
    /// scheme. Behind a TLS-terminating proxy the only thing that knows is X-Forwarded-Proto,
    /// which is why the middleware sits directly behind UseForwardedHeaders.
    /// </summary>
    [Theory]
    [InlineData(Https, true)]
    [InlineData(Http, false)]
    public async Task StrictTransportSecurity_IsSentOnlyOnAForwardedHttpsRequest(
        string scheme,
        bool expected)
    {
        using var app = new KairosApp();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ProxiedOrigin}{ApiPath}");
        request.Headers.Add(ForwardedProtoHeader, scheme);

        var response = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);

        response.Headers.Contains(HeaderNames.StrictTransportSecurity).ShouldBe(expected);
        if (expected)
        {
            // The exact value, so the max-age and the two flags Kairos declines to set — a
            // subdomain pin and a preload entry on a domain it does not own — are both pinned.
            response.Headers.GetValues(HeaderNames.StrictTransportSecurity).Single()
                .ShouldBe(ExpectedMaxAge);
        }
    }

    /// <summary>
    /// The response an operator is most likely to meet while something is already wrong. The
    /// exception handler clears the response and re-executes the pipeline, so a header set written
    /// once on the way in is exactly the set that vanishes here.
    /// </summary>
    [Fact]
    public async Task AnErrorResponse_CarriesTheHeaderSetToo()
    {
        using var app = new KairosApp();

        var response = await app.Client.GetAsync(
            MissingLinkPath,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        ShouldCarryTheSet(response);
    }

    private static void ShouldCarryTheSet(HttpResponseMessage response)
    {
        Policy(response).ShouldContain(FramingRefused, Case.Sensitive);
        Single(response, SecurityHeaders.FrameOptionsHeader).ShouldBe(SecurityHeaders.NoFraming);
        Single(response, SecurityHeaders.ContentTypeOptionsHeader).ShouldBe(SecurityHeaders.NoSniffing);
        Single(response, SecurityHeaders.ReferrerPolicyHeader).ShouldBe(SecurityHeaders.NoReferrer);
        Single(response, SecurityHeaders.PermissionsPolicyHeader).ShouldBe(SecurityHeaders.NoDeviceAccess);
    }

    private static string Policy(HttpResponseMessage response) =>
        Single(response, SecurityHeaders.ContentSecurityPolicyHeader);

    private static string Single(HttpResponseMessage response, string name) =>
        response.Headers.GetValues(name).Single();

    private static string Hash(string script) =>
        $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script)))}'";

    /// <summary>
    /// The application over a web root holding one page, cleaned up the way
    /// <see cref="KairosApp"/> cleans its database. The single page is built by the other CI job
    /// and never by this one, so without this the web root is empty and every assertion about a
    /// policy derived from a page would pass over a page that does not exist.
    /// </summary>
    private static async Task WithSinglePageAsync(Func<KairosApp, Task> assert)
    {
        var webRoot = Directory.CreateTempSubdirectory("kairos-webroot");
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(webRoot.FullName, ContentSecurityPolicy.IndexPage),
                SinglePage,
                TestContext.Current.CancellationToken);

            using var app = new KairosApp(webRoot.FullName);
            await assert(app);
        }
        finally
        {
            webRoot.Delete(recursive: true);
        }
    }
}
