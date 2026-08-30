using System.Text.Json;

using Kairos.Api.Scheduling;

using NodaTime;

using Shouldly;

// NodaTime ships its own Interval; the alias says which one this file means.
using Interval = Kairos.Api.Scheduling.Interval;

namespace Kairos.Api.Tests.Integrations;

/// <summary>
/// The adapter against a canned Google. What these assert on is the outgoing request rather than
/// the parsed answer: asking about one calendar when the operator keeps five is the whole of the
/// defect this seam was built to expose.
/// </summary>
public class GoogleCalendarAdapterTests
{
    private const string FreebusyPath = "/freeBusy";

    /// <summary>Google's alias for the signed-in account's own calendar.</summary>
    private const string PrimaryCalendarAlias = "primary";

    private const string ItemsProperty = "items";

    private const string IdProperty = "id";

    /// <summary>
    /// Nothing busy, keyed by the id that was asked for, which is how Google answers. These tests
    /// are about the question, not the answer.
    /// </summary>
    private const string EmptyFreebusy =
        $$"""
          {
            "calendars": {
              "{{PrimaryCalendarAlias}}": { "busy": [] }
            }
          }
          """;

    /// <summary>2026-09-14T00:00:00Z, and a week of it. Any window; none of this reads a clock.</summary>
    private static readonly Instant WindowStart = Instant.FromUtc(2026, 9, 14, 0, 0);

    private static readonly Instant WindowEnd = Instant.FromUtc(2026, 9, 21, 0, 0);

    private static Interval Window => new(WindowStart, WindowEnd);

    /// <summary>
    /// Today's behaviour, written down before it changes: one freebusy call naming the primary
    /// alias and nothing else, whatever the operator's other calendars hold. The assertion is
    /// inverted rather than deleted when availability learns to read every chosen calendar.
    /// </summary>
    [Fact]
    public async Task GetBusyAsync_WithAConnectedAccount_AsksGoogleAboutThePrimaryCalendarAlone()
    {
        var handler = new FakeGoogleHandler().Answering(FreebusyPath, EmptyFreebusy);
        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);

        await harness.Adapter.GetBusyAsync(Window, TestContext.Current.CancellationToken);

        var freebusy = handler.Requests.ShouldHaveSingleItem();
        freebusy.Path.ShouldContain(FreebusyPath);

        var asked = JsonDocument.Parse(freebusy.Body).RootElement
            .GetProperty(ItemsProperty)
            .EnumerateArray()
            .Select(item => item.GetProperty(IdProperty).GetString())
            .ToList();

        asked.ShouldHaveSingleItem().ShouldBe(PrimaryCalendarAlias);
    }
}
