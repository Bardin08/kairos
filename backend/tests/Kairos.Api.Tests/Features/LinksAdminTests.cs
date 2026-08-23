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
    private const string AnonymousPath = $"{ApiVersions.V1}/sign-in";
    private const string AdminPath = $"{ApiVersions.V1}/admin/links";
    private const string UnmappedApiPath = $"{ApiVersions.V1}/nothing-is-mapped-here";
    private const string SinglePagePath = "/admin";
    private const string SinglePage = "<!doctype html><title>Kairos</title>";

    private const string PatternName = "Weekdays";
    private const string SeededSlug = "mentorship";
    private const string SeededName = "Mentorship";
    private const int DurationMinutes = 30;

    /// <summary>One past the bound, every value distinct and in range, so only the count is wrong.</summary>
    private const int DurationsPastTheCap = SaveLinkRequest.MaxDurations + 1;

    private static readonly Guid PatternId = Guid.CreateVersion7();

    /// <summary>Never seeded, so every route that takes an id has to refuse it.</summary>
    private static readonly Guid MissingLinkId = Guid.CreateVersion7();

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
    /// <c>/api</c> was mapped an unmatched API route answered 200 with the single page, and a
    /// fetch to a mistyped route died on a JSON parse error rather than a 404.
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
            var anonymousApi = await app.Client.GetAsync(AnonymousPath, TestContext.Current.CancellationToken);

            page.StatusCode.ShouldBe(HttpStatusCode.OK);
            missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            // The other half of the fix: a literal segment beats a wildcard, so a mapped route is
            // untouched by the catch-all sitting above it. Sign-in stands in here because it is the
            // cheapest anonymous, literal route under /api — what is under test is route precedence,
            // not sign-in itself.
            anonymousApi.StatusCode.ShouldBe(HttpStatusCode.OK);
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
        app.Seed(context => context.Patterns.Add(new Pattern { Id = PatternId, Name = PatternName }));
        using var client = app.CreateOperatorClient();

        var body = new
        {
            slug = SeededSlug,
            name = SeededName,
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

    /// <summary>
    /// The same unbounded-collection defect the pattern windows carried, one endpoint over: with
    /// no bound the whole array binds and Distinct() walks it, so a body up to Kestrel's 30 MB cap
    /// was allocated and scanned before any duration rule could refuse it. The refusal is the
    /// attribute's, naming the field and carrying no code — every value here passes all three
    /// coded rules, which have nothing to say about a list that is merely long.
    /// </summary>
    [Fact]
    public async Task Create_WithMoreDurationsThanALinkMayOffer_IsRefusedByTheAttribute()
    {
        using var app = new KairosApp();
        app.Seed(context => context.Patterns.Add(new Pattern { Id = PatternId, Name = PatternName }));
        using var client = app.CreateOperatorClient();

        var response = await client.PostAsJsonAsync(
            AdminPath,
            new
            {
                slug = SeededSlug,
                name = SeededName,
                description = string.Empty,
                patternId = PatternId,
                durations = Enumerable.Range(
                    SaveLinkRequest.MinDurationMinutes, DurationsPastTheCap),
            },
            TestContext.Current.CancellationToken);

        var problem = await response.Content.ReadFromJsonAsync<ProblemWithCode>(
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        problem!.Code.ShouldBeNull();
        app.Read(context => context.Links.AsNoTracking().Any()).ShouldBeFalse();
    }

    /// <summary>
    /// An inherited limit is an explicit <c>null</c> on the wire, not a key that is not there.
    /// </summary>
    /// <remarks>
    /// Asserted on the raw body: deserializing into <see cref="AdminLinkResponse"/> would put the
    /// property back whether the JSON carried it or not. What this defends is one line nobody
    /// would connect to it — System.Text.Json's <c>DefaultIgnoreCondition</c> is <c>Never</c>, and
    /// the plausible tidy-up to <c>WhenWritingNull</c> drops the key, so <c>value === null</c>
    /// goes false in <c>InheritableNumber</c> and every inherited limit renders as a box holding
    /// the global, silently stopping tracking at the next save.
    /// </remarks>
    [Fact]
    public async Task ListLinks_ForAnInheritedLimit_SendsAnExplicitNullRatherThanOmittingTheKey()
    {
        using var app = new KairosApp();
        app.Seed(context =>
        {
            context.Patterns.Add(new Pattern { Id = PatternId, Name = PatternName });
            context.Links.Add(new Link
            {
                Slug = SeededSlug,
                Name = SeededName,
                PatternId = PatternId,
                Durations = [new LinkDuration { Minutes = DurationMinutes, SortOrder = 0 }],
            });
        });
        using var client = app.CreateOperatorClient();

        var body = await client.GetStringAsync(AdminPath, TestContext.Current.CancellationToken);

        body.ShouldContain("\"marginMinutes\":null");
    }

    /// <summary>
    /// The link is the resource, so its absence is the first thing an answer can say. The three
    /// body validations ran ahead of the lookup, so a PUT to a guid nothing answers to came back
    /// 409 <c>slug_taken</c>. The other three <c>{id:guid}</c> routes resolve first and 404.
    /// </summary>
    [Fact]
    public async Task Update_ForALinkThatDoesNotExist_IsNotFoundRatherThanAConflictWithAnotherLink()
    {
        using var app = new KairosApp();
        app.Seed(context =>
        {
            context.Patterns.Add(new Pattern { Id = PatternId, Name = PatternName });
            context.Links.Add(new Link
            {
                Slug = SeededSlug,
                Name = SeededName,
                PatternId = PatternId,
                Durations = [new LinkDuration { Minutes = DurationMinutes, SortOrder = 0 }],
            });
        });
        using var client = app.CreateOperatorClient();

        // The slug the seeded link already answers to, so the collision the handler used to report
        // is genuinely there — what is wrong with reporting it is only that the link is not.
        var response = await client.PutAsJsonAsync(
            $"{AdminPath}/{MissingLinkId}",
            new
            {
                slug = SeededSlug,
                name = SeededName,
                description = string.Empty,
                patternId = PatternId,
                durations = new[] { DurationMinutes },
            },
            TestContext.Current.CancellationToken);

        var problem = await response.Content.ReadFromJsonAsync<ProblemWithCode>(
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        problem!.Code.ShouldBe(DomainException.NotFoundCode);
    }

    /// <summary>Only the machine-readable half of ProblemDetails; nothing else is asserted on.</summary>
    private sealed record ProblemWithCode(string Code);
}
