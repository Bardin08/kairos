using Kairos.Api.Persistence.Entities;

using Shouldly;

namespace Kairos.Api.Tests.Integrations;

public sealed class GoogleCredentialRefreshTests
{
    private const int TwoRefreshes = 2;
    private const int NoRefreshes = 0;
    private const string InvalidGrant = "invalid_grant";

    /// <summary>Any instant.</summary>
    private static readonly DateTime Start = new(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan OneAccessTokenLifetime =
        TimeSpan.FromSeconds(FakeGoogleHandler.AccessTokenLifetimeSeconds);

    [Fact]
    public async Task GetCredentialAsync_TheAccessTokenHasExpired_RefreshesItAgain()
    {
        var handler = new FakeGoogleHandler();
        var clock = new SteppedGoogleClock(Start);
        await using var harness = await GoogleAdapterHarness.CreateAsync(handler, clock);

        await harness.Clients.GetCredentialAsync(TestContext.Current.CancellationToken);
        clock.Advance(OneAccessTokenLifetime);
        await harness.Clients.GetCredentialAsync(TestContext.Current.CancellationToken);

        handler.TokenRequests.ShouldBe(TwoRefreshes);
    }

    [Fact]
    public async Task RefreshAsync_GoogleRefusesTheGrant_RecordsReconnectRequiredAndReturns()
    {
        var handler = new FakeGoogleHandler().RefusingGrant();
        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);

        await harness.Clients.RefreshAsync(TestContext.Current.CancellationToken);

        var credential = await harness.ReadCredentialAsync();
        credential.Health.ShouldBe(CredentialHealth.ReconnectRequired);
        credential.LastError.ShouldBe(InvalidGrant);
    }

    [Fact]
    public async Task RefreshAsync_TheGrantWorksAgain_RecordsHealthy()
    {
        var handler = new FakeGoogleHandler().RefusingGrant();
        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);
        await harness.Clients.RefreshAsync(TestContext.Current.CancellationToken);

        handler.RestoringGrant();
        await harness.Clients.RefreshAsync(TestContext.Current.CancellationToken);

        var credential = await harness.ReadCredentialAsync();
        credential.Health.ShouldBe(CredentialHealth.Healthy);
    }

    [Fact]
    public async Task RefreshAsync_TheProviderIsPaused_AsksGoogleNothing()
    {
        var handler = new FakeGoogleHandler();
        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);
        await harness.PauseAsync();

        await harness.Clients.RefreshAsync(TestContext.Current.CancellationToken);

        handler.TokenRequests.ShouldBe(NoRefreshes);
    }
}
