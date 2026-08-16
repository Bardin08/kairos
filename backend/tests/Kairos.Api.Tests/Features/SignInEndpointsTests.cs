using System.Net;
using System.Net.Http.Json;

using Kairos.Api.Features.Admin;
using Kairos.Api.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The one route on the anonymous side of the gate that a refused operator reads, and the
/// configuration behind it.
/// </summary>
public class SignInEndpointsTests
{
    private const string Route = $"{ApiVersions.V1}/sign-in";

    /// <summary>
    /// Both halves of the contract in one call, because they only mean anything together: a
    /// caller with no session must be answered, and answered with this application's own
    /// challenge route. Gated, it would 401 at exactly the reader it exists for; defaulted to
    /// anything else, an operator who configured nothing would be sent nowhere.
    /// </summary>
    [Fact]
    public async Task GetSignIn_WithoutASession_AnswersTheApplicationsOwnLoginRoute()
    {
        using var app = new KairosApp();

        var response = await app.Client.GetAsync(Route, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<SignInResponse>(
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body!.SignInUrl.ShouldBe(SignInEndpoints.DefaultUrl);
    }

    /// <summary>
    /// The override exists so a deployment can send the operator through a portal, a VPN gate or
    /// the provider's own account page first. It is the reason the URL is served rather than
    /// compiled into the bundle, so a default that could not be overridden would make the whole
    /// route pointless.
    /// </summary>
    [Fact]
    public async Task GetSignIn_WithASignInUrlConfigured_AnswersThatInstead()
    {
        const string elsewhere = "https://id.example.test/authorize/kairos";

        using var app = new KairosApp();
        using var configured = app.WithWebHostBuilder(
            builder => builder.UseSetting(SignInEndpoints.ConfigurationKey, elsewhere));

        var body = await configured.CreateClient().GetFromJsonAsync<SignInResponse>(
            Route, TestContext.Current.CancellationToken);

        body!.SignInUrl.ShouldBe(elsewhere);
    }
}
