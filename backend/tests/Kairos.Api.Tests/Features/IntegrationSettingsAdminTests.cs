using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Kairos.Api.Features.Integrations;
using Kairos.Api.Integrations;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Shared;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// What the settings panel is told, for a provider with settings and for one without. The second
/// case is the design under test: a build whose only provider implements no settings capability
/// must still offer the switch, or the unified experience is a claim rather than a fact.
/// </summary>
public class IntegrationSettingsAdminTests
{
    private const string Google = "google";

    /// <summary>
    /// The wire spellings, written out rather than read off the types that mint them. Asserting
    /// against those constants would pass whatever they were renamed to; these three strings are
    /// what the panel keys on.
    /// </summary>
    private const string EnabledKey = "enabled";

    private const string ToggleKind = "toggle";

    private const string UnknownKey = "no-such-field";

    /// <summary>Shaped like the real thing; only the key it was encrypted with would differ.</summary>
    private const string Payload = """{"refreshToken":"1//not-a-real-token"}""";

    private const string AccountLabel = "operator@example.com";

    /// <summary>2026-08-15T22:29:14Z. Any instant; nothing here reads a clock.</summary>
    private const long ConnectedAt = 1_786_832_954;

    private const string IntegrationsPath = $"{ApiVersions.V1}/admin/integrations";

    /// <summary>
    /// A provider nobody connected has no credential row, and the switch is a column on that row.
    /// There is nothing to configure about a connection that does not exist, and the page is told
    /// so in the one way it already knows how to render.
    /// </summary>
    [Fact]
    public async Task ReadSettings_ForAProviderNobodyConnected_IsNotFound()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();

        var response = await client.GetAsync(
            SettingsPath(Google), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The unified experience, stated as a test. Google is the only provider this build has, it is
    /// connected, and whether or not it describes any settings of its own the switch is there.
    /// </summary>
    [Fact]
    public async Task ReadSettings_ForAConnectedProvider_AlwaysOffersTheSwitchFirst()
    {
        using var app = new KairosApp();
        SeedConnectedGoogle(app);
        using var client = app.CreateOperatorClient();

        var response = await client.GetAsync(
            SettingsPath(Google), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var settings = await response.Content.ReadFromJsonAsync<IntegrationSettingsResponse>(
            TestContext.Current.CancellationToken);

        settings.ShouldNotBeNull();
        settings.Provider.ShouldBe(Google);

        var first = settings.Fields[0];
        first.Key.ShouldBe(EnabledKey);
        first.Kind.ShouldBe(ToggleKind);
        first.Value.GetBoolean().ShouldBeTrue();
    }

    /// <summary>
    /// The switch is written to its column, and the listing the page reads must agree — two
    /// screens disagreeing about whether an integration is running is the failure this replaces.
    /// </summary>
    [Fact]
    public async Task SaveSettings_TurningTheProviderOff_ShowsAsPausedOnTheListing()
    {
        using var app = new KairosApp();
        SeedConnectedGoogle(app);
        using var client = app.CreateOperatorClient();

        var save = await client.PutAsJsonAsync(
            SettingsPath(Google),
            new { values = new Dictionary<string, object> { [EnabledKey] = false } },
            TestContext.Current.CancellationToken);

        save.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var listing = await client.GetFromJsonAsync<IntegrationResponse[]>(
            IntegrationsPath, TestContext.Current.CancellationToken);

        listing.ShouldNotBeNull();
        listing.ShouldHaveSingleItem().Enabled.ShouldBeFalse();
    }

    /// <summary>
    /// Refused rather than ignored. A key the provider does not describe is a caller that has
    /// misunderstood the contract, and silently dropping it would let a typo look like a save.
    /// </summary>
    [Fact]
    public async Task SaveSettings_WithAKeyNoFieldDescribes_IsRefusedWithACode()
    {
        using var app = new KairosApp();
        SeedConnectedGoogle(app);
        using var client = app.CreateOperatorClient();

        var response = await client.PutAsJsonAsync(
            SettingsPath(Google),
            new { values = new Dictionary<string, object> { [UnknownKey] = true } },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        problem.GetProperty(ProblemCode.Key).GetString()
            .ShouldBe(IIntegrationSettings.SettingUnknownCode);
    }

    private static string SettingsPath(string provider) =>
        $"{IntegrationsPath}/{provider}/settings";

    private static void SeedConnectedGoogle(KairosApp app) =>
        app.Seed(context => context.IntegrationCredentials.Add(new IntegrationCredential
        {
            Provider = IntegrationProvider.Google,
            ProtectedPayload = Payload,
            AccountLabel = AccountLabel,
            ConnectedAt = ConnectedAt,
            UpdatedAt = ConnectedAt,
        }));
}
