using Kairos.Api.Integrations;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Tests.Features;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Kairos.Api.Tests.Integrations;

/// <summary>
/// The one key no provider may describe. <c>IntegrationSettingsReader</c> throws when it meets a
/// field keyed <see cref="IIntegrationSettings.EnabledKey"/>, and the throw is right — a provider
/// describing the switch is an authorship bug, not an operator's mistake — but nothing can vary it
/// per request, so it is a static property of the registered capabilities that was only ever
/// discovered by somebody opening a settings panel.
/// </summary>
/// <remarks>
/// Written over whatever is registered rather than over one provider, so the second provider is
/// covered by arriving instead of by someone remembering to extend this. That is also why it does
/// not use <see cref="GoogleAdapterHarness"/>: the harness hands back one named adapter, and
/// naming a provider here is the thing this test exists not to do.
/// </remarks>
public class ReservedSettingKeyTests
{
    private const string CalendarListPath = "/users/me/calendarList";

    /// <summary>The connected account, whose address is also its primary calendar's id.</summary>
    private const string AccountLabel = "operator@example.com";

    private const string RefreshToken = "1//not-a-real-token";

    /// <summary>
    /// Enough of an account for a provider to describe itself against. What comes back is not
    /// asserted on — the keys are what this test reads, and a key is not a function of the answer.
    /// </summary>
    private const string CalendarList =
        $$"""
          {"items":[
            {"id":"{{AccountLabel}}","summary":"Operator","primary":true,
             "selected":true,"accessRole":"owner"}
          ]}
          """;

    [Fact]
    public async Task DescribeSettingsAsync_ForEveryRegisteredProvider_DescribesNoFieldKeyedEnabled()
    {
        var handler = new FakeGoogleHandler().Answering(CalendarListPath, CalendarList);
        using var app = new KairosApp(google: handler);
        using var scope = app.Services.CreateScope();

        await ConnectAsync(scope);

        var capabilities = scope.ServiceProvider.GetServices<IIntegrationSettings>().ToList();

        // A registration that moved, or a provider that stopped implementing the capability, would
        // otherwise leave the loop below iterating nothing and this test green about a check it
        // never made.
        capabilities.ShouldNotBeEmpty();

        foreach (var capability in capabilities)
        {
            var fields = await capability.DescribeSettingsAsync(TestContext.Current.CancellationToken);

            fields.ShouldNotBeEmpty();
            fields.Select(field => field.Key).ShouldNotContain(IIntegrationSettings.EnabledKey);
        }
    }

    /// <summary>
    /// A provider describes itself differently once an account is attached, so the fields this
    /// reads are the ones an operator would be looking at rather than a provider's offline shape.
    /// </summary>
    private static Task ConnectAsync(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IntegrationCredentialStore>()
            .WriteAsync(
                IntegrationProvider.Google,
                new StoredPayload(RefreshToken),
                AccountLabel,
                TestContext.Current.CancellationToken);

    /// <summary>The one property the credential store round-trips, restated as the harness does.</summary>
    private sealed record StoredPayload(string RefreshToken);
}
