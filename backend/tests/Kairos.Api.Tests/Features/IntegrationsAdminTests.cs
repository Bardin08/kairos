using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;

using Kairos.Api.Features.Integrations;
using Kairos.Api.Integrations;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Security;
using Kairos.Api.Shared;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// What the Integrations page is told, and the two answers it may never be given: an unexplained
/// 500, and a lie.
/// </summary>
/// <remarks>
/// These exist because the page rendered a red "An error occurred while processing your request."
/// on an instance merely run with two different secret keys. The listing is the only read on that
/// screen, so a failure in it is the whole screen — no table, no button. Its three states must be
/// told apart from outside, and only "the request failed" may be a failed request.
/// </remarks>
public class IntegrationsAdminTests
{
    private const string IntegrationsPath = $"{ApiVersions.V1}/admin/integrations";

    /// <summary>Shaped like the real thing, so the row under test differs only in its key.</summary>
    private const string Payload = """{"refreshToken":"1//not-a-real-token","reconnectRequired":false}""";

    private const string Google = "google";
    private const string AccountLabel = "operator@example.com";

    private const string UnknownProvider = "bogus";

    /// <summary>
    /// What <see cref="IntegrationProviderResolver.UnknownProviderName"/> reads as once
    /// <see cref="DomainException.NotFound"/> has made a sentence of it. Spelled out rather than
    /// composed from the constant, so a change to either half has to be made on purpose.
    /// </summary>
    private const string UnknownProviderTitle = "Integration provider does not exist.";

    /// <summary>2026-08-15T22:29:14Z. Any instant; nothing here reads a clock.</summary>
    private const long ConnectedAt = 1_786_832_954;

    /// <summary>
    /// The ordinary state of a self-hosted Kairos never given a Google client, and the state the
    /// page exists to render: a provider it can name, nothing connected to it. Neither half is a
    /// failure, and the listing must answer 200 saying so.
    /// </summary>
    [Fact]
    public async Task ListIntegrations_WithNoProviderConfiguration_ReportsItUnconfiguredRatherThanFailing()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();

        var response = await client.GetAsync(IntegrationsPath, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var integrations = await response.Content.ReadFromJsonAsync<IntegrationResponse[]>(
            TestContext.Current.CancellationToken);

        var google = integrations.ShouldHaveSingleItem();
        google.Provider.ShouldBe(Google);
        google.Configured.ShouldBeFalse();
        google.Connected.ShouldBeFalse();
    }

    /// <summary>
    /// The 500 this suite was written for, and the dead end that replaced it.
    /// </summary>
    /// <remarks>
    /// A credential encrypted under one Security:SecretKey and read under another throws rather
    /// than decoding to nonsense — AES-GCM authenticates — and the listing decrypts every stored
    /// credential to see whether it works. Escaping as a bug it was the framework's error page;
    /// named as a domain failure, a red sentence with nothing to press. Reported on the row
    /// instead: reconnecting is the fix, and the Connect button needs the listing to answer.
    /// </remarks>
    [Fact]
    public async Task ListIntegrations_WhenTheStoredCredentialWillNotDecrypt_ReportsItOnTheRowAndStillAnswers()
    {
        using var app = new KairosApp();
        SeedCredentialFromAnotherKey(app);
        using var client = app.CreateOperatorClient();

        var response = await client.GetAsync(IntegrationsPath, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var integrations = await response.Content.ReadFromJsonAsync<IntegrationResponse[]>(
            TestContext.Current.CancellationToken);

        var google = integrations.ShouldHaveSingleItem();
        google.Fault.ShouldBe(IntegrationCredentialStore.CredentialUnreadableCode);

        // Connected, because there IS a stored credential. Saying otherwise would read as
        // "nothing was ever linked" about an account the operator remembers linking, and the
        // label is what lets the page name it in the sentence beside the button.
        google.Connected.ShouldBeTrue();
        google.AccountLabel.ShouldBe(AccountLabel);
    }

    /// <summary>
    /// The store's own answer, asserted where the code is minted. Every other reader of a
    /// credential meets this as a named domain failure rather than a cryptography exception,
    /// which is what keeps the cause in a sentence instead of a stack trace.
    /// </summary>
    [Fact]
    public async Task ReadAsync_WithACredentialFromAnotherKey_ThrowsANamedDomainFailure()
    {
        using var app = new KairosApp();
        SeedCredentialFromAnotherKey(app);

        using var scope = app.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IntegrationCredentialStore>();

        var failure = await Should.ThrowAsync<DomainException>(async () =>
            await store.ReadAsync<StoredPayload>(
                IntegrationProvider.Google, TestContext.Current.CancellationToken));

        failure.Code.ShouldBe(IntegrationCredentialStore.CredentialUnreadableCode);
        failure.Message.ShouldContain("Security:SecretKey");
    }

    /// <summary>
    /// The callback lands on the page whatever happened: a human is holding this browser. An
    /// unknown provider was the one exit that threw instead, answering a navigation with
    /// ProblemDetails — raw JSON in the address bar. Redirecting is safe because the page turns
    /// codes into copy and keeps a fallback sentence for one it does not know.
    /// </summary>
    [Fact]
    public async Task CompleteIntegration_ForAProviderThatDoesNotExist_LandsOnTheReturnPageRatherThanAnsweringJson()
    {
        using var app = new KairosApp();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync(
            $"{ApiVersions.V1}/integrations/{UnknownProvider}/callback",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Found);

        var landing = response.Headers.Location!.OriginalString;
        landing.ShouldStartWith(IntegrationEndpoints.ReturnPath);
        landing.ShouldContain(DomainException.NotFoundCode);
    }

    /// <summary>
    /// The connect route's own refusal, which nothing exercised until the provider match moved into
    /// <see cref="IntegrationProviderResolver"/> and left one implementation where there had been
    /// two. This route is fetched rather than navigated to, so it owes a named 404 the page's
    /// Connect button can act on — not the empty 400 the enum binder would have produced, and not
    /// the 500 a nullable lookup used without checking would.
    /// </summary>
    [Fact]
    public async Task ConnectIntegration_ForAProviderThisBuildHasNoConnectorFor_IsNotFoundAboutTheProvider()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();

        var response = await client.PostAsync(
            $"{IntegrationsPath}/{UnknownProvider}/connect",
            content: null,
            TestContext.Current.CancellationToken);

        var problem = await response.Content.ReadFromJsonAsync<ProblemWithCode>(
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        problem!.Code.ShouldBe(DomainException.NotFoundCode);
        problem.Title.ShouldBe(UnknownProviderTitle);
    }

    /// <summary>Shape-compatible with what a provider stores; only the key differs.</summary>
    private sealed record StoredPayload(string RefreshToken, bool ReconnectRequired);

    /// <summary>As every other admin suite spells it: the code the page keys on, and the sentence.</summary>
    private sealed record ProblemWithCode(string Code, string Title);

    /// <summary>
    /// Exactly the state a laptop reaches by connecting under a real key and then running with
    /// the committed development one: a row that is there, and ciphertext this process cannot
    /// open.
    /// </summary>
    private static void SeedCredentialFromAnotherKey(KairosApp app)
    {
        var otherKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretProtector.KeyBytes));

        app.Seed(context => context.IntegrationCredentials.Add(new IntegrationCredential
        {
            Provider = IntegrationProvider.Google,
            ProtectedPayload = new SecretProtector(otherKey).Protect(Payload),
            AccountLabel = AccountLabel,
            ConnectedAt = ConnectedAt,
            UpdatedAt = ConnectedAt,
        }));
    }
}
