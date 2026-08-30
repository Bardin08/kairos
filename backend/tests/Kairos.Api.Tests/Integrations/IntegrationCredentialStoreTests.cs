using System.Security.Cryptography;

using Kairos.Api.Integrations;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Security;
using Kairos.Api.Shared;

using Microsoft.EntityFrameworkCore;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Integrations;

/// <summary>
/// The health half of the credential store: who may write it, what a write costs, and the one
/// transition an operator performs by hand.
/// </summary>
/// <remarks>
/// Written against the store rather than over HTTP because the throttle cannot be observed from
/// outside — a route answering the same either way cannot tell you whether it wrote a row.
/// </remarks>
public class IntegrationCredentialStoreTests
{
    /// <summary>2026-08-19T09:01:12Z. Any instant; nothing here reads a real clock.</summary>
    private const long FirstSuccess = 1_787_130_072;

    /// <summary>Inside SuccessRefreshSeconds, so a second success must write nothing.</summary>
    private const int WithinTheWindowSeconds = 60;

    /// <summary>Past it, so a second success must stamp the row.</summary>
    private const int PastTheWindowSeconds = 600;

    private const string RefreshTokenJson = """{"refreshToken":"1//not-a-real-token"}""";
    private const string RefreshToken = "1//not-a-real-token";
    private const string ReplacementToken = "1//a-newer-token";
    private const string AccountLabel = "operator@example.com";
    private const string InvalidGrant = "invalid_grant";

    [Fact]
    public async Task MarkUnhealthyAsync_ForAConnectedCredential_RecordsTheStateAndTheCodeAndKeepsTheLastSuccess()
    {
        using var database = new TestDatabase();
        var clock = new MovableClock(Instant.FromUnixTimeSeconds(FirstSuccess));
        await using var context = database.CreateContext();
        var store = NewStore(context, clock);

        await SeedConnectedAsync(store, context);

        await store.MarkUnhealthyAsync(
            IntegrationProvider.Google,
            CredentialHealth.ReconnectRequired,
            InvalidGrant,
            TestContext.Current.CancellationToken);

        var row = await ReadRowAsync(database);
        row.Health.ShouldBe(CredentialHealth.ReconnectRequired);
        row.LastError.ShouldBe(InvalidGrant);

        // Kept, deliberately. A failure does not un-happen the last success, and "it last worked
        // on the 19th" is the half of the alert an operator can actually act on.
        row.LastSuccessAt.ShouldBe(FirstSuccess);
    }

    [Fact]
    public async Task MarkHealthyAsync_AfterAFailure_ClearsTheStateAndTheCodeAndStampsTheSuccess()
    {
        using var database = new TestDatabase();
        var clock = new MovableClock(Instant.FromUnixTimeSeconds(FirstSuccess));
        await using var context = database.CreateContext();
        var store = NewStore(context, clock);

        await SeedConnectedAsync(store, context);
        await store.MarkUnhealthyAsync(
            IntegrationProvider.Google,
            CredentialHealth.ReconnectRequired,
            InvalidGrant,
            TestContext.Current.CancellationToken);

        clock.Advance(PastTheWindowSeconds);
        await store.MarkHealthyAsync(IntegrationProvider.Google, TestContext.Current.CancellationToken);

        var row = await ReadRowAsync(database);
        row.Health.ShouldBe(CredentialHealth.Healthy);
        row.LastError.ShouldBeNull();
        row.LastSuccessAt.ShouldBe(FirstSuccess + PastTheWindowSeconds);
    }

    /// <summary>
    /// The throttle, and the only reason it exists: this runs on the token-refresh path, which
    /// runs on every booking page load. Unthrottled it is a SQLite write per page view for a value
    /// nobody read.
    /// </summary>
    [Fact]
    public async Task MarkHealthyAsync_TwiceInsideTheRefreshWindow_WritesNothingTheSecondTime()
    {
        using var database = new TestDatabase();
        var clock = new MovableClock(Instant.FromUnixTimeSeconds(FirstSuccess));
        await using var context = database.CreateContext();
        var store = NewStore(context, clock);

        await SeedConnectedAsync(store, context);
        await store.MarkHealthyAsync(IntegrationProvider.Google, TestContext.Current.CancellationToken);

        var afterFirst = await ReadRowAsync(database);

        clock.Advance(WithinTheWindowSeconds);
        await store.MarkHealthyAsync(IntegrationProvider.Google, TestContext.Current.CancellationToken);

        var afterSecond = await ReadRowAsync(database);
        afterSecond.UpdatedAt.ShouldBe(afterFirst.UpdatedAt);
        afterSecond.LastSuccessAt.ShouldBe(afterFirst.LastSuccessAt);
    }

    /// <summary>
    /// Phase 2 condition 6's "reconnecting clears all three". It would fail if the reset were
    /// forgotten on the connect path — where nobody would notice, because the page says
    /// "connected" either way.
    /// </summary>
    [Fact]
    public async Task WriteAsync_ForACredentialThatWasBroken_ResetsHealthAndClearsTheError()
    {
        using var database = new TestDatabase();
        var clock = new MovableClock(Instant.FromUnixTimeSeconds(FirstSuccess));
        await using var context = database.CreateContext();
        var store = NewStore(context, clock);

        await SeedConnectedAsync(store, context);
        await store.MarkUnhealthyAsync(
            IntegrationProvider.Google,
            CredentialHealth.ReconnectRequired,
            InvalidGrant,
            TestContext.Current.CancellationToken);

        clock.Advance(PastTheWindowSeconds);
        await store.WriteAsync(
            IntegrationProvider.Google,
            new StoredPayload(ReplacementToken),
            AccountLabel,
            TestContext.Current.CancellationToken);

        var row = await ReadRowAsync(database);
        row.Health.ShouldBe(CredentialHealth.Healthy);
        row.LastError.ShouldBeNull();
        row.LastSuccessAt.ShouldBe(FirstSuccess + PastTheWindowSeconds);
    }

    /// <summary>
    /// The readability probe that replaces a decrypt done for another reason. Same named failure
    /// the typed read raises, so the Integrations page keeps the one diagnosis it can act on.
    /// </summary>
    [Fact]
    public async Task EnsureReadableAsync_WithACredentialFromAnotherKey_ThrowsTheNamedDomainFailure()
    {
        using var database = new TestDatabase();
        var clock = new MovableClock(Instant.FromUnixTimeSeconds(FirstSuccess));
        await using var context = database.CreateContext();

        var otherKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretProtector.KeyBytes));
        context.IntegrationCredentials.Add(new IntegrationCredential
        {
            Provider = IntegrationProvider.Google,
            ProtectedPayload = new SecretProtector(otherKey).Protect(RefreshTokenJson),
            AccountLabel = AccountLabel,
            ConnectedAt = FirstSuccess,
            UpdatedAt = FirstSuccess,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();

        var store = NewStore(context, clock);

        var failure = await Should.ThrowAsync<DomainException>(async () =>
            await store.EnsureReadableAsync(
                IntegrationProvider.Google, TestContext.Current.CancellationToken));

        failure.Code.ShouldBe(IntegrationCredentialStore.CredentialUnreadableCode);
    }

    /// <summary>A provider nobody ever connected is not unreadable — it is absent.</summary>
    [Fact]
    public async Task EnsureReadableAsync_WithNoCredentialAtAll_Returns()
    {
        using var database = new TestDatabase();
        var clock = new MovableClock(Instant.FromUnixTimeSeconds(FirstSuccess));
        await using var context = database.CreateContext();
        var store = NewStore(context, clock);

        await Should.NotThrowAsync(async () =>
            await store.EnsureReadableAsync(
                IntegrationProvider.Google, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The whole of the switch. Every adapter reaches its credential through this method, so a
    /// paused provider goes quiet without a line of its own — and a provider added later inherits
    /// that by existing rather than by remembering.
    /// </summary>
    [Fact]
    public async Task ReadAsync_ForADisabledProvider_AnswersNullAsThoughNothingWereConnected()
    {
        using var database = new TestDatabase();
        var clock = new MovableClock(Instant.FromUnixTimeSeconds(FirstSuccess));
        await using var context = database.CreateContext();
        var store = NewStore(context, clock);

        await SeedConnectedAsync(store, context);

        await store.SetEnabledAsync(
            IntegrationProvider.Google, false, TestContext.Current.CancellationToken);

        var payload = await store.ReadAsync<StoredPayload>(
            IntegrationProvider.Google, TestContext.Current.CancellationToken);

        payload.ShouldBeNull();
    }

    /// <summary>
    /// The page has to tell Paused from Not connected, and it reads the row to do it. Gating the
    /// listing as well would collapse the two states into one and lose the operator the sentence
    /// explaining why nothing is syncing.
    /// </summary>
    [Fact]
    public async Task ListAsync_ForADisabledProvider_StillReturnsTheRow()
    {
        using var database = new TestDatabase();
        var clock = new MovableClock(Instant.FromUnixTimeSeconds(FirstSuccess));
        await using var context = database.CreateContext();
        var store = NewStore(context, clock);

        await SeedConnectedAsync(store, context);

        await store.SetEnabledAsync(
            IntegrationProvider.Google, false, TestContext.Current.CancellationToken);

        var rows = await store.ListAsync(TestContext.Current.CancellationToken);

        rows.ShouldHaveSingleItem().Enabled.ShouldBeFalse();
    }

    /// <summary>
    /// Reconnecting is the operator fixing a grant, not resuming an integration they paused on
    /// purpose. The switch is one control away on the same panel; guessing here would be the
    /// upgrade quietly deciding what Kairos considers busy.
    /// </summary>
    [Fact]
    public async Task WriteAsync_OverADisabledProvider_LeavesItDisabled()
    {
        using var database = new TestDatabase();
        var clock = new MovableClock(Instant.FromUnixTimeSeconds(FirstSuccess));
        await using var context = database.CreateContext();
        var store = NewStore(context, clock);

        await SeedConnectedAsync(store, context);

        await store.SetEnabledAsync(
            IntegrationProvider.Google, false, TestContext.Current.CancellationToken);

        await store.WriteAsync(
            IntegrationProvider.Google,
            new StoredPayload(ReplacementToken),
            AccountLabel,
            TestContext.Current.CancellationToken);

        var rows = await store.ListAsync(TestContext.Current.CancellationToken);
        rows.ShouldHaveSingleItem().Enabled.ShouldBeFalse();
    }

    /// <summary>
    /// The switch as the sync path asks about it. A provider nobody connected is not paused, and
    /// collapsing the two is what turns a deliberate pause into a Failed calendar entry saying no
    /// account is connected.
    /// </summary>
    [Fact]
    public async Task IsPausedAsync_ForAProviderNobodyConnected_AnswersFalseRatherThanPaused()
    {
        using var database = new TestDatabase();
        var clock = new MovableClock(Instant.FromUnixTimeSeconds(FirstSuccess));
        await using var context = database.CreateContext();
        var store = NewStore(context, clock);

        var paused = await store.IsPausedAsync(
            IntegrationProvider.Google, TestContext.Current.CancellationToken);

        paused.ShouldBeFalse();
    }

    /// <summary>
    /// The other side of the same answer, and the expensive one to get wrong: a provider reported
    /// paused when it is not stops every calendar write on the deployment, silently and with no
    /// row anywhere saying why.
    /// </summary>
    [Fact]
    public async Task IsPausedAsync_ForAConnectedProviderThatIsOn_AnswersFalse()
    {
        using var database = new TestDatabase();
        var clock = new MovableClock(Instant.FromUnixTimeSeconds(FirstSuccess));
        await using var context = database.CreateContext();
        var store = NewStore(context, clock);

        await SeedConnectedAsync(store, context);

        var paused = await store.IsPausedAsync(
            IntegrationProvider.Google, TestContext.Current.CancellationToken);

        paused.ShouldBeFalse();
    }

    [Fact]
    public async Task IsPausedAsync_ForAProviderTheOperatorSwitchedOff_AnswersTrue()
    {
        using var database = new TestDatabase();
        var clock = new MovableClock(Instant.FromUnixTimeSeconds(FirstSuccess));
        await using var context = database.CreateContext();
        var store = NewStore(context, clock);

        await SeedConnectedAsync(store, context);
        await store.SetEnabledAsync(
            IntegrationProvider.Google, false, TestContext.Current.CancellationToken);

        var paused = await store.IsPausedAsync(
            IntegrationProvider.Google, TestContext.Current.CancellationToken);

        paused.ShouldBeTrue();
    }

    /// <summary>
    /// The switch has no throttle to justify skipping a write, unlike <c>MarkHealthyAsync</c>: it
    /// is pressed by hand. Returning early on a value already set would leave UpdatedAt saying the
    /// row was last touched at some earlier moment, which is the one thing that column claims.
    /// </summary>
    [Fact]
    public async Task SetEnabledAsync_ForAValueAlreadySet_StampsTheRowAnyway()
    {
        using var database = new TestDatabase();
        var clock = new MovableClock(Instant.FromUnixTimeSeconds(FirstSuccess));
        await using var context = database.CreateContext();
        var store = NewStore(context, clock);

        await SeedConnectedAsync(store, context);
        await store.SetEnabledAsync(
            IntegrationProvider.Google, false, TestContext.Current.CancellationToken);

        clock.Advance(PastTheWindowSeconds);
        await store.SetEnabledAsync(
            IntegrationProvider.Google, false, TestContext.Current.CancellationToken);

        var row = await ReadRowAsync(database);
        row.Enabled.ShouldBeFalse();
        row.UpdatedAt.ShouldBe(FirstSuccess + PastTheWindowSeconds);
    }

    /// <summary>Shaped like a provider's payload; nothing here reads what is inside it.</summary>
    private sealed record StoredPayload(string RefreshToken);

    private static IntegrationCredentialStore NewStore(KairosDbContext context, IClock clock) =>
        new(context, new SecretProtector(TestKey()), clock);

    /// <summary>
    /// A key of the right length, and deliberately not a random one: every test here that reads a
    /// payload back is testing the store, not the cipher.
    /// </summary>
    private static string TestKey() =>
        Convert.ToBase64String(new byte[SecretProtector.KeyBytes]);

    private static async Task SeedConnectedAsync(IntegrationCredentialStore store, KairosDbContext context)
    {
        await store.WriteAsync(
            IntegrationProvider.Google,
            new StoredPayload(RefreshToken),
            AccountLabel,
            TestContext.Current.CancellationToken);

        context.ChangeTracker.Clear();
    }

    /// <summary>A second context, so what is asserted is what reached the file.</summary>
    private static async Task<IntegrationCredential> ReadRowAsync(TestDatabase database)
    {
        await using var context = database.CreateContext();
        return await context.IntegrationCredentials.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A clock a test can push forward, on the FixedClock pattern IcsBuilderTests already uses.
    /// The throttle is a comparison against a timestamp, so a real clock would make that test
    /// either meaningless or slow.
    /// </summary>
    private sealed class MovableClock(Instant now) : IClock
    {
        private Instant _now = now;

        public Instant GetCurrentInstant() => _now;

        public void Advance(int seconds) => _now = _now.Plus(Duration.FromSeconds(seconds));
    }
}
