using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;

using Kairos.Api.Features.Admin;
using Kairos.Api.Features.Integrations;
using Kairos.Api.Integrations;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

using NodaTime;
using NodaTime.Text;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// What the workbench is told about things that are broken, and the two answers it may never be
/// given: a fault nobody can act on, and a fault that is not there.
/// </summary>
public class AlertsAdminTests
{
    private const string AlertsPath = $"{ApiVersions.V1}/admin/alerts";

    private const string Google = "google";
    private const string AccountLabel = "operator@example.com";
    private const string AccountKey = "account";
    private const string InvalidGrant = "invalid_grant";

    /// <summary>2026-08-19T07:41:12Z. Any instant; nothing here reads a clock.</summary>
    private const long LastSuccess = 1_787_130_072;

    /// <summary>Shaped like a stored credential. Nothing in these tests decrypts it.</summary>
    private const string Payload = """{"refreshToken":"1//not-a-real-token"}""";

    private const string Slug = "consulting";
    private const string LinkName = "Consulting call";
    private const int DurationMinutes = 25;
    private const int NineAm = 9 * 60;
    private const int FivePm = 17 * 60;
    private const string FirstSlotTime = "T09:00:00Z";

    /// <summary>Days ahead of seeding, so the real-clock minimum notice never decides a test.</summary>
    private const int WellAhead = 30;

    private static readonly Guid PatternId = Guid.CreateVersion7();
    private static readonly Guid LinkId = Guid.CreateVersion7();

    [Fact]
    public async Task ListAlerts_WithACredentialNeedingReconnection_NamesTheProviderTheAccountAndTheWayBack()
    {
        using var app = new KairosApp();
        SeedCredential(app, CredentialHealth.ReconnectRequired, LastSuccess, AccountLabel);
        using var client = app.CreateOperatorClient();

        var response = await client.GetAsync(AlertsPath, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var alerts = await response.Content.ReadFromJsonAsync<AlertResponse[]>(
            TestContext.Current.CancellationToken);

        var alert = alerts.ShouldHaveSingleItem();
        alert.Code.ShouldBe(IntegrationAlertSource.ReconnectRequiredCode);
        alert.Severity.ShouldBe(AlertSeverity.Error);
        alert.Subject.ShouldBe(Google);
        alert.Link.ShouldBe(IntegrationEndpoints.ReturnPath);
        alert.Details[AccountKey].ShouldBe(AccountLabel);
        alert.Since.ShouldBe(
            InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(LastSuccess)));
    }

    /// <summary>The absence of a label is not the absence of an alert.</summary>
    [Fact]
    public async Task ListAlerts_ForACredentialWithNoAccountLabel_StillAlertsWithNoDetails()
    {
        using var app = new KairosApp();
        SeedCredential(app, CredentialHealth.ReconnectRequired, LastSuccess, accountLabel: null);
        using var client = app.CreateOperatorClient();

        var alerts = await client.GetFromJsonAsync<AlertResponse[]>(
            AlertsPath, TestContext.Current.CancellationToken);

        alerts.ShouldHaveSingleItem().Details.ShouldBeEmpty();
    }

    /// <summary>
    /// A connected credential nobody has used yet is not a fault. This is what stops the panel
    /// crying on a fresh install — the fastest way to teach an operator to ignore it.
    /// </summary>
    [Fact]
    public async Task ListAlerts_WithAHealthyCredentialThatHasNeverSucceeded_AnswersAnEmptyList()
    {
        using var app = new KairosApp();
        SeedCredential(app, CredentialHealth.Healthy, lastSuccessAt: null, AccountLabel);
        using var client = app.CreateOperatorClient();

        var alerts = await client.GetFromJsonAsync<AlertResponse[]>(
            AlertsPath, TestContext.Current.CancellationToken);

        alerts.ShouldBeEmpty();
    }

    /// <summary>Rule 19 binds the mapping site; this is the test that it was obeyed.</summary>
    [Fact]
    public async Task ListAlerts_WithoutASession_IsRefused()
    {
        using var app = new KairosApp();

        var response = await app.Client.GetAsync(AlertsPath, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The guard, and why it is not left to each source's good manners: this value becomes an href
    /// on a page the operator trusts.
    /// </summary>
    [Fact]
    public async Task ListAlerts_WhenASourceOffersAnAbsoluteLink_AnswersItAsNoLinkAtAll()
    {
        using var app = new KairosApp();
        using var client = app.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                    services.AddScoped<IOperatorAlertSource, ElsewhereSource>()))
            .CreateOperatorClient();

        var alerts = await client.GetFromJsonAsync<AlertResponse[]>(
            AlertsPath, TestContext.Current.CancellationToken);

        alerts.ShouldNotBeNull();
        alerts.ShouldContain(alert => alert.Link == null);
    }

    /// <summary>
    /// Ground rule 14 and phase 2 condition 6's third clause in one test. A booking the domain
    /// accepted is never lost because a third party's grant died — and the alert stands afterwards,
    /// so the operator learns while the invitee is told nothing.
    /// </summary>
    [Fact]
    public async Task PostBooking_WhileTheGrantIsDead_IsAcceptedAndTheAlertStands()
    {
        using var app = new KairosApp();
        SeedCredential(app, CredentialHealth.ReconnectRequired, LastSuccess, AccountLabel);
        SeedBookableLink(app);

        var booking = await app.Client.PostAsJsonAsync(
            $"{ApiVersions.V1}/bookings",
            new
            {
                slug = Slug,
                start = Day() + FirstSlotTime,
                durationMinutes = DurationMinutes,
                name = "Vlad",
                email = "vlad@example.com",
                timeZone = "Europe/Warsaw",
            },
            TestContext.Current.CancellationToken);

        booking.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var client = app.CreateOperatorClient();
        var alerts = await client.GetFromJsonAsync<AlertResponse[]>(
            AlertsPath, TestContext.Current.CancellationToken);

        alerts.ShouldHaveSingleItem().Code.ShouldBe(IntegrationAlertSource.ReconnectRequiredCode);
    }

    /// <summary>
    /// A dead grant must never take the pod out of the load balancer. The probes belong to the
    /// proxy task; this asserts that the alert surface did not quietly enlist a third party's OAuth
    /// grant into readiness.
    /// </summary>
    [Fact]
    public async Task Readyz_WithADeadCredential_StillAnswersReady()
    {
        using var app = new KairosApp();
        SeedCredential(app, CredentialHealth.ReconnectRequired, LastSuccess, AccountLabel);

        var response = await app.Client.GetAsync("/readyz", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>A source that would send the operator's browser to somebody else's origin.</summary>
    private sealed class ElsewhereSource : IOperatorAlertSource
    {
        private const string Elsewhere = "https://example.com/elsewhere";
        private const string TestCode = "test_alert";

        public Task<IReadOnlyList<OperatorAlert>> CollectAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OperatorAlert>>(
            [
                new OperatorAlert(
                    TestCode,
                    AlertSeverity.Warning,
                    null,
                    null,
                    Elsewhere,
                    ImmutableDictionary<string, string>.Empty),
            ]);
    }

    /// <summary>
    /// A Monday well ahead of today, computed rather than written down: the application reads the
    /// real clock, so a literal date makes a test that passes until it silently stops.
    /// </summary>
    private static string Day() => LocalDatePattern.Iso.Format(
        SystemClock.Instance.GetCurrentInstant().InUtc().Date.PlusDays(WellAhead).Next(IsoDayOfWeek.Monday));

    /// <summary>One pattern, one Monday nine-to-five rule, one active link, one duration.</summary>
    private static void SeedBookableLink(KairosApp app) => app.Seed(context =>
    {
        context.Patterns.Add(new Pattern { Id = PatternId, Name = "Weekdays" });
        context.PatternRules.Add(new PatternRule
        {
            Id = Guid.CreateVersion7(),
            PatternId = PatternId,
            IsoDayOfWeek = (int)IsoDayOfWeek.Monday,
            StartMinute = NineAm,
            EndMinute = FivePm,
        });
        context.Links.Add(new Link
        {
            Id = LinkId,
            Slug = Slug,
            Name = LinkName,
            PatternId = PatternId,
            Active = true,
        });
        context.LinkDurations.Add(new LinkDuration
        {
            LinkId = LinkId,
            Minutes = DurationMinutes,
            SortOrder = 0,
        });
    });

    private static void SeedCredential(
        KairosApp app,
        CredentialHealth health,
        long? lastSuccessAt,
        string? accountLabel)
    {
        app.Seed(context => context.IntegrationCredentials.Add(new IntegrationCredential
        {
            Provider = IntegrationProvider.Google,
            ProtectedPayload = Payload,
            AccountLabel = accountLabel,
            Health = health,
            LastSuccessAt = lastSuccessAt,
            LastError = health == CredentialHealth.Healthy ? null : InvalidGrant,
            ConnectedAt = LastSuccess,
            UpdatedAt = LastSuccess,
        }));
    }
}
