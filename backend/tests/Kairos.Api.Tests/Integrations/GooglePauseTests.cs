using NodaTime;

using Shouldly;

// NodaTime ships its own Interval; the alias says which one this file means.
using Interval = Kairos.Api.Scheduling.Interval;

namespace Kairos.Api.Tests.Integrations;

/// <summary>
/// The pause switch against a process that has already talked to Google. A cold process proves
/// nothing here: the credential store's gate answers correctly on a first read, and the whole of
/// the defect was that no second read ever happened — <c>GoogleClientFactory</c> caches the
/// credential and only reads the store when the cache is empty, so a paused provider kept
/// querying until a restart.
/// </summary>
public class GooglePauseTests
{
    private const string FreebusyPath = "/freeBusy";

    /// <summary>
    /// One busy hour inside the window, so the warm call has something to hand back. Asserting on
    /// what came out of it is what tells a query Google really answered from one the adapter
    /// swallowed: <c>GetBusyAsync</c> catches everything and returns an empty list, so a test
    /// counting requests alone reads a refused call and a satisfied one identically.
    /// </summary>
    private const string OneBusyHour =
        """
        {
          "calendars": {
            "primary": { "busy": [{ "start": "2026-09-15T09:00:00Z", "end": "2026-09-15T10:00:00Z" }] }
          }
        }
        """;

    /// <summary>Freebusy queries once the first has gone out: one, whatever else was asked.</summary>
    private const int QueriedOnce = 1;

    /// <summary>2026-09-14T00:00:00Z, and a week of it. Any window; none of this reads a clock.</summary>
    private static readonly Instant WindowStart = Instant.FromUtc(2026, 9, 14, 0, 0);

    private static readonly Instant WindowEnd = Instant.FromUtc(2026, 9, 21, 0, 0);

    /// <summary>The hour <see cref="OneBusyHour"/> reports, as the adapter has to translate it.</summary>
    private static readonly Instant BusyStart = Instant.FromUtc(2026, 9, 15, 9, 0);

    private static readonly Instant BusyEnd = Instant.FromUtc(2026, 9, 15, 10, 0);

    private static Interval Window => new(WindowStart, WindowEnd);

    /// <summary>
    /// The defect, stated as the operator sees it: a provider paused on a running instance stops
    /// being asked anything, without a restart. The first query is what makes the test worth
    /// having — it warms the cached credential the second one has to get past, and the busy hour
    /// it returns is the proof that it warmed rather than failed quietly.
    /// </summary>
    [Fact]
    public async Task GetBusyAsync_AfterTheProviderIsPausedOnAWarmProcess_AsksGoogleNothingFurther()
    {
        var handler = new FakeGoogleHandler().Answering(FreebusyPath, OneBusyHour);
        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);

        var warm = await harness.Adapter.GetBusyAsync(Window, TestContext.Current.CancellationToken);

        var busy = warm.ShouldHaveSingleItem();
        busy.Interval.Start.ShouldBe(BusyStart);
        busy.Interval.End.ShouldBe(BusyEnd);
        handler.Requests.Count(request => request.Path.Contains(FreebusyPath))
            .ShouldBe(QueriedOnce);

        // Whatever else the warm call cost — a deployment that has never chosen its calendars
        // works the default out from the calendar list, once — is the baseline the pause has to
        // add nothing to. A total counted here rather than a literal, so this stays a test about
        // pausing rather than one that fails whenever the first read's shape changes.
        var askedWhileRunning = handler.Requests.Count;

        await harness.PauseAsync();

        var paused = await harness.Adapter.GetBusyAsync(Window, TestContext.Current.CancellationToken);

        // Both halves, because either alone is satisfied by the wrong thing: an empty list is also
        // what a refused query returns, and a request count is also unchanged by a query that went
        // out and was answered with nothing.
        paused.ShouldBeEmpty();
        handler.Requests.Count.ShouldBe(askedWhileRunning);
    }
}
