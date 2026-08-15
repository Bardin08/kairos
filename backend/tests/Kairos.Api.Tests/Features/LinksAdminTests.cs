using System.Net;
using System.Net.Http.Json;

using Kairos.Api.Features.Admin;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Shared;

using Microsoft.EntityFrameworkCore;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// What has to hold around the links admin slice: the gate answers, the API prefix ends where it
/// says it does, and a link can never be left with nothing to offer.
/// </summary>
public class LinksAdminTests
{
    private const string PublicPath = $"{ApiVersions.V1}/links";
    private const string AdminPath = $"{ApiVersions.V1}/admin/links";
    private const string UnmappedApiPath = $"{ApiVersions.V1}/nothing-is-mapped-here";
    private const string SinglePagePath = "/admin";
    private const string SinglePage = "<!doctype html><title>Kairos</title>";

    private static readonly Guid PatternId = Guid.CreateVersion7();

    [Fact]
    public async Task ListLinks_WithoutASession_IsUnauthorizedRatherThanARedirect()
    {
        using var app = new KairosApp();

        var response = await app.Client.GetAsync(AdminPath, TestContext.Current.CancellationToken);

        // 401 and not 302: the workbench reaches this with fetch, and a fetch cannot follow a
        // redirect to the identity provider's origin — it fails as an opaque CORS error that says
        // nothing about the session having expired.
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// <c>MapFallbackToFile</c> claims every extensionless path, so before the catch-all under
    /// <c>/api</c> was mapped an unmatched API route answered 200 with the single page. The failure
    /// is silent in both directions — a path nothing serves answered 200, and a fetch to a mistyped
    /// route received <c>&lt;!doctype html&gt;</c> and died on a JSON parse error rather than on a
    /// 404 — and the next person to add a route will not be thinking about route ordering.
    /// </summary>
    [Fact]
    public async Task UnmappedApiPath_IsNotFound_WhileTheSinglePageStillAnswers()
    {
        var webRoot = Directory.CreateTempSubdirectory("kairos-webroot");
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(webRoot.FullName, "index.html"),
                SinglePage,
                TestContext.Current.CancellationToken);

            using var app = new KairosApp(webRoot.FullName);

            // The precondition, asserted rather than assumed: the fallback is a no-op without an
            // index.html on disk, and without one this whole test would pass either way.
            var page = await app.Client.GetAsync(SinglePagePath, TestContext.Current.CancellationToken);
            var missing = await app.Client.GetAsync(UnmappedApiPath, TestContext.Current.CancellationToken);
            var publicApi = await app.Client.GetAsync(PublicPath, TestContext.Current.CancellationToken);

            page.StatusCode.ShouldBe(HttpStatusCode.OK);
            missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            // The other half of the fix: a literal segment beats a wildcard, so a mapped public
            // route is untouched by the catch-all sitting above it.
            publicApi.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            webRoot.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The rule that spans two tables and therefore cannot be a SQLite CHECK, asserted where it
    /// actually lives. Unenforced, nothing complains until the public booking page loads and dies
    /// inside <c>Durations.Default</c> on an index that names nothing.
    /// </summary>
    [Fact]
    public async Task Create_WithNoDurations_IsRefusedRatherThanWritingAnUnbookableLink()
    {
        using var app = new KairosApp();
        app.Seed(context => context.Patterns.Add(new Pattern { Id = PatternId, Name = "Weekdays" }));
        using var client = app.CreateOperatorClient();

        var body = new
        {
            slug = "mentorship",
            name = "Mentorship",
            description = "",
            patternId = PatternId,
            durations = Array.Empty<int>(),
        };

        var response = await client.PostAsJsonAsync(
            AdminPath, body, TestContext.Current.CancellationToken);
        var problem = await response.Content.ReadFromJsonAsync<ProblemWithCode>(
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        problem!.Code.ShouldBe(LinksAdminEndpoints.DurationsInvalidCode);
        app.Read(context => context.Links.AsNoTracking().Any()).ShouldBeFalse();
    }

    /// <summary>Only the machine-readable half of ProblemDetails; nothing else is asserted on.</summary>
    private sealed record ProblemWithCode(string Code);
}
