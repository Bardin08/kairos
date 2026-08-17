using System.Net;

using Kairos.Api.Features.Integrations;
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

}
