using System.Net;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Text;

using Kairos.Api.Features.Integrations;
using Kairos.Api.Integrations;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Security;
using Kairos.Api.Shared;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// What the settings panel is told about one provider, and what one Save leaves behind when part
/// of it is refused.
/// </summary>
/// <remarks>
/// Google is the only provider this build has. The switch is the endpoint's own and comes first
/// whatever the adapter describes — that is what makes the experience one experience — and
/// everything after it is the provider's, down to the sentence a key it does not describe is
/// refused in. What that adapter accepts is its own suite's subject, not this one's.
/// </remarks>
public class IntegrationSettingsAdminTests
{
    private const string Google = "google";

    /// <summary>The same provider, typed the way a URL pasted out of a document arrives.</summary>
    private const string ShoutedGoogle = "GOOGLE";

    /// <summary>No connector answers to it, so it is what the resolver's 404 is reached with.</summary>
    private const string UnknownProvider = "bogus";

    /// <summary>
    /// The wire spellings, written out rather than read off the types that mint them. Asserting
    /// against those constants would pass whatever they were renamed to; these three strings are
    /// what the panel keys on.
    /// </summary>
    private const string EnabledKey = "enabled";

    private const string ToggleKind = "toggle";

    /// <summary>Described by no provider, so a save carrying it has to be refused by name.</summary>
    private const string UnknownKey = "no-such-field";

    /// <summary>
    /// The two 404s these routes answer, written out rather than read off the resolver, which
    /// keeps one of them private. Both carry <c>not_found</c>, so the sentence is the only thing
    /// telling an operator reading a log which of the two happened.
    /// </summary>
    private const string UnknownProviderTitle = "Integration provider does not exist.";

    private const string NoConnectionTitle = "A connection to that integration does not exist.";

    /// <summary>
    /// Shaped like the real thing, and stored the way the application stores it — sealed, so the
    /// row under test is one this deployment can read. In the clear it was not a credential at
    /// all: the listing decoded it, failed, and reported <c>credential_unreadable</c> beneath
    /// assertions that passed anyway.
    /// </summary>
    private const string Payload = """{"refreshToken":"1//not-a-real-token"}""";

    private const string AccountLabel = "operator@example.com";

    /// <summary>The key present and holding null, which a typed request instance cannot express.</summary>
    private const string NullValuesBody = """{"values":null}""";

    /// <summary>2026-08-15T22:29:14Z. Any instant; nothing here reads a clock.</summary>
    private const long ConnectedAt = 1_786_832_954;

    private const string IntegrationsPath = $"{ApiVersions.V1}/admin/integrations";

    /// <summary>
    /// A provider nobody connected has no credential row, and the switch is a column on that row.
    /// There is nothing to configure about a connection that does not exist, and the page is told
    /// so in the one way it already knows how to render.
    /// </summary>
    [Fact]
    public async Task ReadSettings_ForAProviderNobodyConnected_IsNotFoundAboutTheConnection()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();

        var response = await client.GetAsync(
            SettingsPath(Google), TestContext.Current.CancellationToken);

        var problem = await ShouldBeRefusedAsync(
            response, HttpStatusCode.NotFound, DomainException.NotFoundCode);

        problem.Title.ShouldBe(NoConnectionTitle);
    }

    /// <summary>
    /// The other 404, and the reason it is worth its own sentence: a provider this build cannot
    /// name and one nobody has connected are different things for an operator to do something
    /// about, and both answer <c>not_found</c>. Nothing reached the resolver's unknown-provider
    /// branch before this — the connected-provider tests all name a provider that exists.
    /// </summary>
    [Fact]
    public async Task ReadSettings_ForAProviderThisBuildHasNoConnectorFor_IsNotFoundAboutTheProvider()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();

        var response = await client.GetAsync(
            SettingsPath(UnknownProvider), TestContext.Current.CancellationToken);

        var problem = await ShouldBeRefusedAsync(
            response, HttpStatusCode.NotFound, DomainException.NotFoundCode);

        problem.Title.ShouldBe(UnknownProviderTitle);
    }

    /// <summary>The same segment on the write route, which resolves it through the same call.</summary>
    [Fact]
    public async Task SaveSettings_ForAProviderThisBuildHasNoConnectorFor_IsNotFoundAboutTheProvider()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();

        var response = await client.PutAsJsonAsync(
            SettingsPath(UnknownProvider),
            new { values = new Dictionary<string, object>() },
            TestContext.Current.CancellationToken);

        var problem = await ShouldBeRefusedAsync(
            response, HttpStatusCode.NotFound, DomainException.NotFoundCode);

        problem.Title.ShouldBe(UnknownProviderTitle);
    }

    /// <summary>
    /// The unified experience, stated as a test. Google is the only provider this build has, it is
    /// connected, and whether or not it describes any settings of its own the switch is there.
    /// </summary>
    [Fact]
    public async Task ReadSettings_ForAConnectedProvider_AlwaysOffersTheSwitchFirst()
    {
        using var app = new KairosApp();
        using var client = ConnectedGoogleClient(app);

        var response = await client.GetAsync(
            SettingsPath(Google), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var settings = await ReadSettingsAsync(response);

        var first = settings.Fields[0];
        first.Key.ShouldBe(EnabledKey);
        first.Kind.ShouldBe(ToggleKind);
        first.Value.GetBoolean().ShouldBeTrue();
    }

    /// <summary>
    /// The whole argument for matching the segment against the connectors rather than letting the
    /// binder parse the enum: the binder's parse is case-sensitive, so a capitalised segment
    /// answered an empty 400 with no code on it. The response names the provider lower case
    /// whichever way it was asked for, because that spelling is the contract.
    /// </summary>
    [Theory]
    [InlineData(Google)]
    [InlineData(ShoutedGoogle)]
    public async Task ReadSettings_WhateverCaseTheSegmentIsIn_AnswersForTheSameProvider(string segment)
    {
        using var app = new KairosApp();
        using var client = ConnectedGoogleClient(app);

        var response = await client.GetAsync(
            SettingsPath(segment), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var settings = await ReadSettingsAsync(response);
        settings.Provider.ShouldBe(Google);
    }

    /// <summary>
    /// The switch is written to its column, and the listing the page reads must agree — two
    /// screens disagreeing about whether an integration is running is the failure this replaces.
    /// </summary>
    [Fact]
    public async Task SaveSettings_TurningTheProviderOff_ShowsAsPausedOnTheListing()
    {
        using var app = new KairosApp();
        using var client = ConnectedGoogleClient(app);

        var save = await client.PutAsJsonAsync(
            SettingsPath(Google),
            new { values = new Dictionary<string, object> { [EnabledKey] = false } },
            TestContext.Current.CancellationToken);

        save.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var google = await ShouldListASingleIntegrationAsync(client);

        google.Enabled.ShouldBeFalse();

        // The seeded row's real state, pinned rather than assumed: the listing decrypts every
        // credential, so a fixture it cannot read reports a fault beside an Enabled that is
        // perfectly correct — which is how a plaintext payload sat here unnoticed.
        google.Fault.ShouldBeNull();
    }

    /// <summary>
    /// One Save, one outcome. The panel sends the whole map, so a pause travelling beside a value
    /// the provider refuses must leave the switch alone: a 400 under a panel reading "Save failed"
    /// while the integration is genuinely off is the worst answer available.
    /// </summary>
    [Fact]
    public async Task SaveSettings_PausingBesideARefusedValue_LeavesTheSwitchAsItWas()
    {
        using var app = new KairosApp();
        using var client = ConnectedGoogleClient(app);

        var response = await client.PutAsJsonAsync(
            SettingsPath(Google),
            new
            {
                values = new Dictionary<string, object>
                {
                    [EnabledKey] = false,
                    [UnknownKey] = true,
                },
            },
            TestContext.Current.CancellationToken);

        await ShouldBeRefusedAsync(
            response, HttpStatusCode.BadRequest, IIntegrationSettings.SettingUnknownCode);

        (await ShouldListASingleIntegrationAsync(client)).Enabled.ShouldBeTrue();
    }

    /// <summary>
    /// Refused rather than ignored: a key no field describes is a caller that has misunderstood
    /// the contract, and dropping it silently would let a typo look like a save. The sentence has
    /// to name the key — one code answers every unknown one, so it is the only thing telling the
    /// operator which of the values they sent was the problem.
    /// </summary>
    [Fact]
    public async Task SaveSettings_WithAKeyNoFieldDescribes_IsRefusedNamingTheKey()
    {
        using var app = new KairosApp();
        using var client = ConnectedGoogleClient(app);

        var response = await client.PutAsJsonAsync(
            SettingsPath(Google),
            new { values = new Dictionary<string, object> { [UnknownKey] = true } },
            TestContext.Current.CancellationToken);

        var problem = await ShouldBeRefusedAsync(
            response, HttpStatusCode.BadRequest, IIntegrationSettings.SettingUnknownCode);

        problem.Title.ShouldContain(UnknownKey);
    }

    /// <summary>
    /// The map present and holding null, which the request record says is refused. System.Text.Json
    /// ignores the nullable annotation, so without the attribute the writer walks a null map and
    /// the operator gets a 500 — the surprise
    /// <see cref="Kairos.Api.Features.Admin.CreateInviteRequest.Prefill"/> was documented for.
    /// </summary>
    [Fact]
    public async Task SaveSettings_WithTheValueMapSentAsNull_IsRefusedRatherThanFaulting()
    {
        using var app = new KairosApp();
        using var client = ConnectedGoogleClient(app);

        // Written as JSON rather than through the record: the defect is in what the binder does
        // with an explicit null, which a typed instance cannot express.
        var response = await client.PutAsync(
            SettingsPath(Google),
            new StringContent(NullValuesBody, Encoding.UTF8, MediaTypeNames.Application.Json),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static async Task<IntegrationSettingsResponse> ReadSettingsAsync(
        HttpResponseMessage response)
    {
        var settings = await response.Content.ReadFromJsonAsync<IntegrationSettingsResponse>(
            TestContext.Current.CancellationToken);

        return settings.ShouldNotBeNull();
    }

    private static async Task<IntegrationResponse> ShouldListASingleIntegrationAsync(HttpClient client)
    {
        var listing = await client.GetFromJsonAsync<IntegrationResponse[]>(
            IntegrationsPath, TestContext.Current.CancellationToken);

        listing.ShouldNotBeNull();
        return listing.ShouldHaveSingleItem();
    }

    private static async Task<ProblemWithCode> ShouldBeRefusedAsync(
        HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var problem = await response.Content.ReadFromJsonAsync<ProblemWithCode>(
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(status);
        problem!.Code.ShouldBe(code);

        return problem;
    }

    private static string SettingsPath(string provider) =>
        $"{IntegrationsPath}/{provider}/settings";

    /// <summary>
    /// A connected Google, and an operator client whose host reads it with the same
    /// <c>Security:SecretKey</c> that sealed it.
    /// </summary>
    private static HttpClient ConnectedGoogleClient(KairosApp app)
    {
        var factory = app.OperatorFactory();
        var protector = factory.Services.GetRequiredService<SecretProtector>();

        app.Seed(context => context.IntegrationCredentials.Add(new IntegrationCredential
        {
            Provider = IntegrationProvider.Google,
            ProtectedPayload = protector.Protect(Payload),
            AccountLabel = AccountLabel,
            ConnectedAt = ConnectedAt,
            UpdatedAt = ConnectedAt,
        }));

        return factory.CreateClient();
    }

    /// <summary>
    /// The two halves of a refusal a caller acts on: the code it branches on, and the sentence it
    /// shows. <see cref="DomainExceptionHandler"/> writes the domain message as the title.
    /// </summary>
    private sealed record ProblemWithCode(string Code, string Title);
}
