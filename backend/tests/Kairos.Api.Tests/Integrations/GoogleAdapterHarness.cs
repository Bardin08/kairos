using System.Text.Json;

using Kairos.Api.Integrations;
using Kairos.Api.Integrations.Google;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Tests.Features;

using Microsoft.Extensions.DependencyInjection;

namespace Kairos.Api.Tests.Integrations;

/// <summary>
/// A real <see cref="GoogleCalendarAdapter"/>, resolved out of the real composition root, with a
/// connected Google account and a fake Google behind it.
/// </summary>
/// <remarks>
/// Built on <see cref="KairosApp"/> rather than by hand. Hand-wiring the adapter would mean
/// retyping the composition root into the test project, and the copy goes stale silently — the
/// failure being a registration that changes in Program.cs while every adapter test keeps passing
/// against the old shape.
/// </remarks>
internal sealed class GoogleAdapterHarness : IAsyncDisposable
{
    /// <summary>The connected account. Its address is also the primary calendar's id.</summary>
    private const string AccountLabel = "operator@example.com";

    private const string RefreshToken = "1//not-a-real-token";

    private readonly KairosApp _app;
    private readonly IServiceScope _scope;
    private readonly IntegrationSettingStore _settings;

    private GoogleAdapterHarness(KairosApp app, IServiceScope scope)
    {
        _app = app;
        _scope = scope;
        Adapter = scope.ServiceProvider.GetRequiredService<GoogleCalendarAdapter>();
        _settings = scope.ServiceProvider.GetRequiredService<IntegrationSettingStore>();
    }

    public GoogleCalendarAdapter Adapter { get; }

    public static async Task<GoogleAdapterHarness> CreateAsync(FakeGoogleHandler handler)
    {
        var app = new KairosApp(google: handler);
        var scope = app.Services.CreateScope();
        var harness = new GoogleAdapterHarness(app, scope);

        // Written through the store, never as a row of its own: the payload has to be encrypted
        // with this deployment's SecretProtector, and a hand-built row would decrypt to
        // credential_unreadable rather than to a connected account.
        await scope.ServiceProvider.GetRequiredService<IntegrationCredentialStore>()
            .WriteAsync(
                IntegrationProvider.Google,
                new StoredPayload(RefreshToken),
                AccountLabel,
                TestContext.Current.CancellationToken);

        return harness;
    }

    /// <summary>Writes the saved selection the way the endpoint would, straight to the store.</summary>
    public Task SaveBusyCalendarsAsync(params string[] calendars) =>
        _settings.WriteAsync(
            IntegrationProvider.Google,
            new Dictionary<string, JsonElement>
            {
                [GoogleCalendarAdapter.BusyCalendarsKey] =
                    JsonSerializer.SerializeToElement(calendars),
            },
            TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync()
    {
        _scope.Dispose();
        _app.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The adapter's own payload shape, restated. <c>GoogleCredentialPayload</c> is private and
    /// nested inside <see cref="GoogleClientFactory"/> — deliberately, since it crosses no
    /// signature — so what is shared here is the one property name the store round-trips.
    /// </summary>
    private sealed record StoredPayload(string RefreshToken);
}
