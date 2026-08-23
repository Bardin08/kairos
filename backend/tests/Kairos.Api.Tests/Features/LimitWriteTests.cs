using System.Net.Http.Json;

using Kairos.Api.Persistence.Entities;
using Kairos.Api.Shared;

using Microsoft.EntityFrameworkCore;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// Two tests, and the whole reason this file exists. Part A built the global-then-override design
/// on null meaning inherit and zero meaning a deliberate zero; if the write side cannot keep them
/// apart the design collapses silently into a default-copying bug.
/// </summary>
/// <remarks>
/// Over HTTP rather than against the handler, because the distinction has to survive the wire: a
/// body carrying <c>"marginMinutes": null</c> has to arrive as null, a fact about the binder as
/// much as the handler. There is deliberately no test that a link can be renamed.
/// </remarks>
public class LimitWriteTests
{
    private const int SeededMarginMinutes = 30;
    private const int DurationMinutes = 30;

    private static readonly Guid PatternId = Guid.CreateVersion7();
    private static readonly Guid LinkId = Guid.CreateVersion7();

    [Fact]
    public async Task Update_MarginSetToNull_LeavesTheColumnNullSoTheGlobalStillApplies()
    {
        // The bug this guards: writing the resolved value (15) instead of null. The link then
        // looks identical in the UI and stops tracking the global forever.
        using var app = NewApp();
        using var client = app.CreateOperatorClient();

        await UpdateLimitsAsync(client, marginMinutes: null);

        app.Read(context => context.Links.AsNoTracking().Single(link => link.Id == LinkId))
            .MarginMinutes.ShouldBeNull();
    }

    [Fact]
    public async Task Update_MarginSetToZero_StoresZeroRatherThanNull()
    {
        using var app = NewApp();
        using var client = app.CreateOperatorClient();

        await UpdateLimitsAsync(client, marginMinutes: 0);

        app.Read(context => context.Links.AsNoTracking().Single(link => link.Id == LinkId))
            .MarginMinutes.ShouldBe(0);
    }

    /// <summary>
    /// Serialized by hand rather than from the request record, so the body says <c>null</c> on the
    /// wire exactly as the editor sends it. Binding a record to itself would prove nothing about
    /// what a browser puts in the socket.
    /// </summary>
    private static async Task UpdateLimitsAsync(HttpClient client, int? marginMinutes)
    {
        var body = new
        {
            marginMinutes,
            minNoticeMinutes = (int?)null,
            dailyCap = (int?)null,
            startIncrementMinutes = (int?)null,
        };

        var response = await client.PutAsJsonAsync(
            $"{ApiVersions.V1}/admin/links/{LinkId}/limits", body, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>A link with an override already on it, so "unchanged" cannot pass for "cleared".</summary>
    private static KairosApp NewApp()
    {
        var app = new KairosApp();

        app.Seed(context =>
        {
            context.Patterns.Add(new Pattern { Id = PatternId, Name = "Weekdays" });
            context.Links.Add(new Link
            {
                Id = LinkId,
                Slug = "mentorship",
                Name = "Mentorship",
                PatternId = PatternId,
                MarginMinutes = SeededMarginMinutes,
                Durations = [new LinkDuration { Minutes = DurationMinutes, SortOrder = 0 }],
            });
        });

        return app;
    }
}
