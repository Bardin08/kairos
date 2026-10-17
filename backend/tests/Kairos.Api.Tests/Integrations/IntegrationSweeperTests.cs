using Kairos.Api.Integrations;
using Kairos.Api.Persistence.Entities;

using Shouldly;

namespace Kairos.Api.Tests.Integrations;

public sealed class IntegrationSweeperTests
{
    private const int TwoRefreshes = 2;

    /// <summary>
    /// Enough meetings that a sweep placing only some of them would show, and few enough for one
    /// pass to place them all.
    /// </summary>
    private const int IncidentMeetings = 10;

    /// <summary>Both the insert and the read-back of the conference go to a path under this.</summary>
    private const string EventsPath = "/events";

    /// <summary>Any id: the conference read-back asks by booking, so this one is only recorded.</summary>
    private const string ExternalEventId = "e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0";

    private const string ConferenceId = "abc-defg-hij";

    private const string JoinUrl = "https://meet.example.com/abc-defg-hij";

    /// <summary>
    /// An event once Google has provisioned its Meet, answered to the insert and to the read-back
    /// alike, so every meeting gets the same join URL.
    /// </summary>
    private const string EventWithConference =
        $$"""
          {
            "id":"{{ExternalEventId}}",
            "conferenceData":{
              "conferenceId":"{{ConferenceId}}",
              "entryPoints":[{"entryPointType":"video","uri":"{{JoinUrl}}"}]
            }
          }
          """;

    /// <summary>Any instant.</summary>
    private static readonly DateTime Start = new(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The token's own expiry, which Google.Apis treats as stale whatever its refresh window is,
    /// so the advance does not depend on where the library draws that window.
    /// </summary>
    private static readonly TimeSpan NearExpiry =
        TimeSpan.FromSeconds(FakeGoogleHandler.AccessTokenLifetimeSeconds);

    /// <summary>
    /// A credential near expiry is refreshed with no booking in between. Nothing but the sweep
    /// touches the process here, so a second refresh can only be the sweep's.
    /// </summary>
    [Fact]
    public async Task SweepAsync_TheAccessTokenNearsExpiryWithNoTraffic_RefreshesIt()
    {
        var handler = new FakeGoogleHandler();
        var clock = new SteppedGoogleClock(Start);
        await using var harness = await GoogleAdapterHarness.CreateAsync(handler, clock);
        var sweeper = harness.Resolve<IntegrationSweeper>();

        await sweeper.SweepAsync(TestContext.Current.CancellationToken);
        clock.Advance(NearExpiry);
        await sweeper.SweepAsync(TestContext.Current.CancellationToken);

        handler.TokenRequests.ShouldBe(TwoRefreshes);
        handler.Requests.ShouldBeEmpty();
    }

    /// <summary>
    /// The grant dies, meetings are booked into it and refused, the operator reconnects, and one
    /// sweep later every meeting has an event and a join link.
    /// </summary>
    [Fact]
    public async Task SweepAsync_TheGrantRecoversAfterRefusedBookings_GivesEveryMeetingAJoinUrl()
    {
        IncidentMeetings.ShouldBeLessThanOrEqualTo(CalendarSyncService.MaxResyncPerPass);
        var handler = new FakeGoogleHandler()
            .Answering(EventsPath, EventWithConference)
            .RefusingGrant();
        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);
        var bookings = harness.SeedUpcomingBookings(IncidentMeetings);
        foreach (var booking in bookings)
        {
            await harness.SyncAsync(booking);
        }

        harness.ReadEntries().ShouldAllBe(entry => entry.State == CalendarSyncState.Failed);

        handler.RestoringGrant();
        await harness.Resolve<IntegrationSweeper>().SweepAsync(TestContext.Current.CancellationToken);

        var links = harness.ReadConferenceLinks();
        links.Select(link => link.BookingId).ShouldBe(bookings.Select(booking => booking.Id), ignoreOrder: true);
        links.ShouldAllBe(link => link.JoinUrl == JoinUrl);
    }
}
