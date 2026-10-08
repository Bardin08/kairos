using System.Net;
using System.Net.Http.Json;

using Kairos.Api.Features.Legal;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Shared;

using Microsoft.EntityFrameworkCore;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The operator's half of the privacy policy and terms. Anonymous, because the people who read
/// those pages hold no session — an invitee, or Google's reviewer.
/// </summary>
public class LegalEndpointTests
{
    private const string LegalPath = $"{ApiVersions.V1}/legal";
    private const string OperatorName = "Ada Operator";
    private const string ContactEmail = "privacy@kairos.test";
    private const string Jurisdiction = "Poland";

    [Fact]
    public async Task Get_DetailsSaved_ReturnsThemAnonymously()
    {
        using var app = new KairosApp();
        app.Seed(context =>
        {
            var settings = context.Settings.Single(row => row.Id == Settings.SingletonId);
            settings.OperatorName = OperatorName;
            settings.ContactEmail = ContactEmail;
            settings.Jurisdiction = Jurisdiction;
        });

        var response = await app.Client.GetAsync(LegalPath, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<LegalResponse>(TestContext.Current.CancellationToken);
        body.ShouldBe(new LegalResponse(OperatorName, ContactEmail, Jurisdiction));
    }

    [Fact]
    public async Task Get_NothingSaved_ReturnsNulls()
    {
        using var app = new KairosApp();

        var body = await app.Client.GetFromJsonAsync<LegalResponse>(LegalPath, TestContext.Current.CancellationToken);

        body.ShouldBe(new LegalResponse(null, null, null));
    }
}
