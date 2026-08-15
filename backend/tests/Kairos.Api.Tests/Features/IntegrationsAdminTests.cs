using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

using Kairos.Api.Features.Integrations;
using Kairos.Api.Integrations;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Security;
using Kairos.Api.Shared;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// What the Integrations page is told, and the two answers it may never be given: an unexplained
/// 500, and a lie.
/// </summary>
/// <remarks>
/// Both tests exist because the page rendered a red "An error occurred while processing your
/// request." on an instance that had merely been run with two different secret keys. The listing
/// is the one read on that screen, so a failure in it is the whole screen — which is why the
/// difference between "nothing is configured" and "something is wrong" has to be visible from
/// outside the process.
/// </remarks>
public class IntegrationsAdminTests
{
    private const string IntegrationsPath = $"{ApiVersions.V1}/admin/integrations";

    /// <summary>Shaped like the real thing, so the row under test differs only in its key.</summary>
    private const string Payload = """{"refreshToken":"1//not-a-real-token","reconnectRequired":false}""";

    private const string Google = "google";
    private const string AccountLabel = "operator@example.com";

    /// <summary>2026-08-14T22:29:14Z. Any instant; nothing here reads a clock.</summary>
    private const long ConnectedAt = 1_786_832_954;

    /// <summary>
    /// The ordinary state of a self-hosted Kairos that has never been given a Google client, and
    /// the state the page exists to render: a provider it can name, and nothing connected to it.
    /// Neither half is a failure, and the listing must answer 200 saying so.
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
    /// The 500 this suite was written for. A credential encrypted under one Security:SecretKey and
    /// read under another does not decode to nonsense — AES-GCM authenticates, so it throws, and
    /// the listing decrypts every stored credential to find out whether it still works. Escaping
    /// as a bug, it reached the operator as the framework's own "An error occurred while
    /// processing your request.", which names nothing and fixes nothing.
    /// </summary>
    [Fact]
    public async Task ListIntegrations_WhenTheStoredCredentialWillNotDecrypt_SaysSoRatherThanAnsweringFiveHundred()
    {
        using var app = new KairosApp();
        SeedCredentialFromAnotherKey(app);
        using var client = app.CreateOperatorClient();

        var response = await client.GetAsync(IntegrationsPath, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldNotBe(HttpStatusCode.InternalServerError);

        var problem = await ReadProblemAsync(response);
        problem.GetProperty("code").GetString()
            .ShouldBe(IntegrationCredentialStore.CredentialUnreadableCode);

        // The operator learns the cause and the recovery from the screen itself. Asserted on the
        // sentence rather than only on the code, because the code is what a client branches on
        // and the title is the only thing a human ever reads.
        problem.GetProperty("title").ToString().ShouldContain("Security:SecretKey");
    }

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

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
}
