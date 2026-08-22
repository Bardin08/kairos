using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Kairos.Api.Features.Integrations;
using Kairos.Api.Security;
using Kairos.Api.Shared;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// Which requests need a session and which do not. The split is one line in two files, and it is
/// the line that is wrong for weeks without anyone noticing — an over-gated public route breaks
/// booking loudly, but an under-gated operator route breaks nothing and protects nothing.
/// </summary>
public class AdminBoundaryTests
{
    /// <summary>A group the identity provider issues for something that is not this application.</summary>
    private const string ForeignGroup = "some-other-group";

    /// <summary>A second one, so a membership list can be a list rather than a pair.</summary>
    private const string YetAnotherGroup = "a-third-group";

    /// <summary>An account the allowlist does not name, carrying the right group all the same.</summary>
    private const string AnotherSubject = "another-operator";

    private const string FirstSubjectPath = $"{AdminAuthorization.SubjectsPath}:0";

    /// <summary>The claim type an OIDC provider might spell with a capital, and often does.</summary>
    private const string CapitalisedGroupClaimType = "Groups";

    /// <summary>
    /// Every shape of the group claim that has to be decided one way rather than the other, with
    /// the answer beside it. Derived from <see cref="AdminAuthorization.DefaultGroup"/> rather
    /// than written out, so a row says which near miss it is instead of asking a reader to spot
    /// the difference between two literals.
    /// </summary>
    public static TheoryData<string[], HttpStatusCode> GroupClaims => new()
    {
        { [AdminAuthorization.DefaultGroup], HttpStatusCode.OK },
        { [AdminAuthorization.DefaultGroup.ToUpperInvariant()], HttpStatusCode.Forbidden },
        { [$" {AdminAuthorization.DefaultGroup} "], HttpStatusCode.Forbidden },
        { [$"a {AdminAuthorization.DefaultGroup} b"], HttpStatusCode.Forbidden },
        { [ForeignGroup, AdminAuthorization.DefaultGroup, YetAnotherGroup], HttpStatusCode.OK },
    };

    /// <summary>
    /// The shape Tasks 3 to 6 hang their endpoints off, asserted where it is defined rather than
    /// through a route this branch would have to invent to have something to call.
    /// </summary>
    [Fact]
    public void MapAdmin_MapsUnderTheVersionedAdminPrefix_AndRequiresTheAdminPolicy()
    {
        using var app = WebApplication.CreateSlimBuilder().Build();

        app.MapAdmin().MapGet("/links", () => Results.Empty);

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single();

        endpoint.RoutePattern.RawText.ShouldBe($"{ApiVersions.V1}/admin/links");
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .ShouldContain(data => data.Policy == AdminGroup.Policy);
    }

    /// <summary>
    /// Part B's operator routes were written "behind the admin boundary" before one existed, and
    /// answered 200 to anyone when no token was configured. This is the promise being kept, now
    /// under the prefix as well as under the policy.
    /// </summary>
    [Fact]
    public async Task ListIntegrations_WithoutASession_IsUnauthorizedRatherThanARedirect()
    {
        using var app = new KairosApp();

        var response = await app.Client.GetAsync(
            $"{ApiVersions.V1}/admin/integrations", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The old path answers nothing at all.
    /// </summary>
    /// <remarks>
    /// Worth pinning because the failure it guards against is quiet: a route left mapped in both
    /// places is a second, ungated way in that no test hits, and the whole reason these two moved
    /// is that <see cref="AdminGroup"/>'s docstring has to be readable as the whole answer to
    /// "what is public?".
    /// </remarks>
    [Fact]
    public async Task ListIntegrations_AtItsOldPathOutsideThePrefix_IsNotMappedAtAll()
    {
        using var app = new KairosApp();

        var response = await app.Client.GetAsync(
            $"{ApiVersions.V1}/integrations", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The callback is the one route on the other side of that decision, and gating it would
    /// break consent rather than protect it: Google redirects a browser here, and the ticket it
    /// presents is <c>state</c>. Answered here without one, so what is pinned is that the request
    /// reached the handler at all.
    /// </summary>
    [Fact]
    public async Task CompleteIntegration_WithoutASession_StaysAnonymousAndRefusesOnState()
    {
        using var app = new KairosApp();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync(
            $"{ApiVersions.V1}/integrations/google/callback", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location!.OriginalString.ShouldContain(IntegrationEndpoints.StateInvalidCode);
    }

    /// <summary>
    /// The finding this task exists for: the policy was RequireAuthenticatedUser() and nothing
    /// else, so every account the identity provider would authenticate was a full operator. An
    /// OIDC provider is shared across applications by definition, so that is the provider's user
    /// list, not the operator's.
    /// </summary>
    [Fact]
    public async Task ListIntegrations_WithASessionCarryingNoGroup_IsForbiddenRatherThanAnswered()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient(groups: []);

        var response = await client.GetAsync(
            $"{ApiVersions.V1}/admin/integrations", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        // Carried in the body rather than left to the status code, because the workbench draws a
        // different screen for this than for a session that ran out, and 403 alone does not say
        // which of the two happened.
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        problem.GetProperty(ProblemCode.Key).GetString().ShouldBe(AdminAuthorization.NotAnOperatorCode);
    }

    /// <summary>
    /// The other half, and the one that would pass by accident if the policy refused everybody:
    /// the configured group is admitted.
    /// </summary>
    [Fact]
    public async Task ListIntegrations_WithASessionCarryingTheConfiguredGroup_IsAnswered()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();

        var response = await client.GetAsync(
            $"{ApiVersions.V1}/admin/integrations", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// A group the provider does hand out, to somebody who is not an operator here. The half that
    /// a policy asking only "is there a group claim?" would answer 200 to, and the reason the
    /// value is compared rather than the presence of the claim.
    /// </summary>
    [Fact]
    public async Task ListIntegrations_WithASessionCarryingAnotherApplicationsGroup_IsForbidden()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient(groups: [ForeignGroup]);

        var response = await client.GetAsync(
            $"{ApiVersions.V1}/admin/integrations", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// How the group claim is compared, pinned row by row, because none of it is Kairos's own
    /// code and all of it is Kairos's decision. <c>HasClaim(type, value)</c> compares the value
    /// with <see cref="StringComparer.Ordinal"/>, and that is the behaviour wanted: a group name
    /// is an opaque identifier the provider chose, so a near miss is a different group and not a
    /// typo to be forgiven.
    /// </summary>
    /// <remarks>
    /// Unpinned, the rows below are what a well-meaning edit takes away. Making the comparison
    /// case-insensitive, or splitting the value on spaces to support a provider that packs every
    /// membership into one claim, would each ship green today — and the second would admit
    /// <c>"a kairos-users b"</c>, which is a string an untrusted account can often choose.
    /// </remarks>
    [Theory]
    [MemberData(nameof(GroupClaims))]
    public async Task ListIntegrations_WithTheseGroupClaims_IsAnsweredOnlyOnAnExactOrdinalMatch(
        string[] groups,
        HttpStatusCode expected)
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient(groups: groups);

        var response = await client.GetAsync(
            $"{ApiVersions.V1}/admin/integrations", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(expected);
    }

    /// <summary>
    /// The one widening in the read, and it is on the key rather than on the value: claim TYPES
    /// are matched case-insensitively, so a provider that spells the claim <c>Groups</c> is
    /// admitted. Worth pinning because it is the half somebody tightening the comparison above
    /// would tighten by accident.
    /// </summary>
    [Fact]
    public async Task ListIntegrations_WithTheGroupUnderADifferentlyCasedClaimType_IsAnswered()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient(groupClaimType: CapitalisedGroupClaimType);

        var response = await client.GetAsync(
            $"{ApiVersions.V1}/admin/integrations", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The second gate, for the deployment whose provider hands the group out more widely than
    /// Kairos should trust. Written, it decides on top of the group rather than instead of it.
    /// </summary>
    /// <remarks>
    /// The session spells its subject the way a real one does — the OIDC handler maps inbound
    /// claims by default, so <c>sub</c> reaches the principal as the ClaimTypes URI. This is the
    /// spelling every deployment produces, and the fallback arm of the read is what serves it.
    /// </remarks>
    [Fact]
    public async Task ListIntegrations_WithTheGroupAndAListedSubject_IsAnswered()
    {
        using var app = new KairosApp();
        using var client = app
            .WithWebHostBuilder(builder => builder.UseSetting(FirstSubjectPath, OperatorSession.Subject))
            .CreateOperatorClient();

        var response = await client.GetAsync(
            $"{ApiVersions.V1}/admin/integrations", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The other spelling, from a provider configured not to map its claims, where the subject
    /// arrives as the <c>sub</c> the token wrote. Pinned separately because the two spellings are
    /// two arms of one read, and a deployment reaches only ever one of them.
    /// </summary>
    [Fact]
    public async Task ListIntegrations_WithAListedSubjectSpelledAsTheTokenWroteIt_IsAnswered()
    {
        using var app = new KairosApp();
        using var client = app
            .WithWebHostBuilder(builder => builder.UseSetting(FirstSubjectPath, OperatorSession.Subject))
            .CreateOperatorClient(subjectClaimType: AdminAuthorization.SubjectClaimType);

        var response = await client.GetAsync(
            $"{ApiVersions.V1}/admin/integrations", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The same allowlist naming somebody else. Carrying the group is no longer enough, which is
    /// the whole of what an allowlist buys and the only thing that distinguishes it from nothing.
    /// </summary>
    [Fact]
    public async Task ListIntegrations_WithTheGroupAndAnUnlistedSubject_IsForbidden()
    {
        using var app = new KairosApp();
        using var client = app
            .WithWebHostBuilder(builder => builder.UseSetting(FirstSubjectPath, AnotherSubject))
            .CreateOperatorClient();

        var response = await client.GetAsync(
            $"{ApiVersions.V1}/admin/integrations", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A blanked group is a deployment that fumbled its configuration, and it stops the process
    /// rather than answering 403 on every admin route. Asserted on the exception because there is
    /// no request to make against a host that did not build; the base exception is read rather
    /// than the outer one, since the factory surfaces a startup failure wrapped.
    /// </summary>
    [Fact]
    public void CreatingTheHost_WithABlankedAdminGroup_FailsAtStartupAndNamesTheSetting()
    {
        using var app = new KairosApp();

        var thrown = Should.Throw<Exception>(() => app
            .WithWebHostBuilder(builder => builder.UseSetting(AdminAuthorization.GroupPath, ""))
            .CreateClient());

        thrown.GetBaseException().Message.ShouldContain(AdminAuthorization.GroupPath);
    }

    /// <summary>
    /// And a blanked entry in the allowlist, which is the same mistake one setting over: it
    /// matches no principal, so it would lock every operator out silently rather than loudly.
    /// </summary>
    [Fact]
    public void CreatingTheHost_WithABlankedAllowedSubject_FailsAtStartupAndNamesTheSetting()
    {
        using var app = new KairosApp();

        var thrown = Should.Throw<Exception>(() => app
            .WithWebHostBuilder(builder => builder.UseSetting(FirstSubjectPath, ""))
            .CreateClient());

        thrown.GetBaseException().Message.ShouldContain(AdminAuthorization.SubjectsPath);
    }
}
