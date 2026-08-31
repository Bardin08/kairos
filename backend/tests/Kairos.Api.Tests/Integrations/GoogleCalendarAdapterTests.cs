using System.Text.Json;

using Kairos.Api.Integrations;
using Kairos.Api.Integrations.Google;
using Kairos.Api.Scheduling;
using Kairos.Api.Shared;

using NodaTime;
using NodaTime.Text;

using Shouldly;

// NodaTime ships its own Interval; the alias says which one this file means.
using Interval = Kairos.Api.Scheduling.Interval;

namespace Kairos.Api.Tests.Integrations;

/// <summary>
/// The adapter against a canned Google. What these assert on is the outgoing request as much as
/// the parsed answer: asking about one calendar when the operator keeps five was the whole of the
/// defect this seam was built to expose, and the question is where the fix for it lives.
/// </summary>
public class GoogleCalendarAdapterTests
{
    private const string FreebusyPath = "/freeBusy";

    private const string CalendarListPath = "/users/me/calendarList";

    /// <summary>Shared by the insert, the patch, the delete and the read-back.</summary>
    private const string EventsPath = "/events";

    /// <summary>Google's alias for the signed-in account's own calendar.</summary>
    private const string PrimaryCalendarAlias = "primary";

    private const string ItemsProperty = "items";

    private const string IdProperty = "id";

    /// <summary>The connected account's own calendar, which Google lists by its address.</summary>
    private const string PrimaryCalendarAddress = "operator@example.com";

    private const string SecondaryCalendarAddress = "side@example.com";

    /// <summary>The same calendar as a request path spells it; Google escapes the address.</summary>
    private static readonly string SecondaryCalendarInPath =
        Uri.EscapeDataString(SecondaryCalendarAddress);

    private const string SubscribedCalendarAddress = "team@example.com";

    /// <summary>Saved once and gone since — unshared, deleted, or renamed out from under us.</summary>
    private const string DepartedCalendarAddress = "gone@example.com";

    /// <summary>A key no field on any provider describes.</summary>
    private const string UnknownKey = "no-such-field";

    /// <summary>Two views of the public booking page — the second is where a recompute shows.</summary>
    private const int PageLoads = 2;

    /// <summary>Once per deployment rather than once per page load, which is the whole design.</summary>
    private const int CalendarListCalls = 1;

    /// <summary>
    /// One owned primary, one owned secondary Google does not currently show, and one read-only
    /// subscription — enough to separate all three filters the adapter applies.
    /// </summary>
    private const string CalendarList =
        $$"""
          {"items":[
            {"id":"{{PrimaryCalendarAddress}}","summary":"Operator","primary":true,
             "selected":true,"accessRole":"owner"},
            {"id":"{{SecondaryCalendarAddress}}","summary":"Side projects",
             "selected":false,"accessRole":"owner"},
            {"id":"{{SubscribedCalendarAddress}}","summary":"Team",
             "selected":true,"accessRole":"reader"}
          ]}
          """;

    /// <summary>The same account with nothing ticked in Google's own sidebar.</summary>
    private const string NothingSelectedCalendarList =
        $$"""
          {"items":[
            {"id":"{{PrimaryCalendarAddress}}","summary":"Operator","primary":true,
             "selected":false,"accessRole":"owner"},
            {"id":"{{SecondaryCalendarAddress}}","summary":"Side projects",
             "selected":false,"accessRole":"owner"}
          ]}
          """;

    /// <summary>
    /// Every calendar ticked, one more than a single freebusy query may name. Built rather than
    /// written out, because fifty-one literals would say the same thing fifty-one times.
    /// </summary>
    private static readonly string OverCapCalendarList =
        SelectedCalendarList(GoogleCalendarAdapter.MaxFreebusyCalendars + 1);

    /// <summary>2026-09-14T00:00:00Z, and a week of it. Any window; none of this reads a clock.</summary>
    private static readonly Instant WindowStart = Instant.FromUtc(2026, 9, 14, 0, 0);

    private static readonly Instant WindowEnd = Instant.FromUtc(2026, 9, 21, 0, 0);

    private static Interval Window => new(WindowStart, WindowEnd);

    /// <summary>2026-09-15T13:00:00Z, and an hour of it. Inside <see cref="Window"/>, and any.</summary>
    private static readonly Instant BusyStart = Instant.FromUtc(2026, 9, 15, 13, 0);

    private static readonly Instant BusyEnd = Instant.FromUtc(2026, 9, 15, 14, 0);

    /// <summary>The same two instants as Google writes them, so the fixture states them once.</summary>
    private static readonly string BusyFrom = InstantPattern.ExtendedIso.Format(BusyStart);

    private static readonly string BusyTo = InstantPattern.ExtendedIso.Format(BusyEnd);

    /// <summary>
    /// One busy hour, under one calendar id, which is how Google keys its answer. These tests are
    /// about the question rather than the answer — but a block has to come back, because the empty
    /// list is also what <see cref="GoogleCalendarAdapter"/> returns when the call failed and a
    /// suite that only asserted no exception would pass against a swallowed one.
    /// </summary>
    private static readonly string OneBusyPeriod =
        $$"""
          {
            "calendars": {
              "{{PrimaryCalendarAddress}}": {
                "busy": [{ "start": "{{BusyFrom}}", "end": "{{BusyTo}}" }]
              }
            }
          }
          """;

    /// <summary>Any booking; the id only has to be a legal Google event id, which every GUID is.</summary>
    private static readonly Guid BookingId = Guid.Parse("0f7b1c9a-6b2d-4e3f-8a1b-2c3d4e5f6071");

    /// <summary>
    /// The same booking as Google spells an event id — derived rather than retyped, because the
    /// adapter asks for the event back under exactly this id and a second spelling of it could
    /// drift from the first.
    /// </summary>
    private static readonly string ExternalEventId = BookingId.ToString("N");

    private const string ConferenceId = "abc-defg-hij";

    private const string JoinUrl = "https://meet.example.com/abc-defg-hij";

    /// <summary>An event, as far as an insert and a patch need one back.</summary>
    private static readonly string InsertedEvent =
        $$"""{"id":"{{ExternalEventId}}","status":"confirmed"}""";

    /// <summary>What a delete answers with: nothing at all.</summary>
    private const string NoBody = "";

    /// <summary>The same event once Google has finished provisioning the Meet on it.</summary>
    private static readonly string EventWithConference =
        $$"""
          {
            "id":"{{ExternalEventId}}",
            "conferenceData":{
              "conferenceId":"{{ConferenceId}}",
              "entryPoints":[{"entryPointType":"video","uri":"{{JoinUrl}}"}]
            }
          }
          """;

    /// <summary>
    /// The reported bug, as a test. Kairos offered slots the operator was not free for because it
    /// asked Google about one calendar out of several, and everything downstream was already
    /// right — so what this asserts on is the question. It asserts on the answer as well, because
    /// a request that went out and then failed would leave the first half of it true.
    /// </summary>
    [Fact]
    public async Task GetBusyAsync_WithSeveralCalendarsChosen_AsksFreebusyForEveryOneOfThem()
    {
        var handler = new FakeGoogleHandler().Answering(FreebusyPath, OneBusyPeriod);
        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);
        await harness.SaveBusyCalendarsAsync(PrimaryCalendarAddress, SecondaryCalendarAddress);

        var busy = await harness.Adapter.GetBusyAsync(
            Window, TestContext.Current.CancellationToken);

        var freebusy = handler.Requests.ShouldHaveSingleItem();
        freebusy.Path.ShouldContain(FreebusyPath);
        AskedFor(freebusy.Body).ShouldBe(
            [PrimaryCalendarAddress, SecondaryCalendarAddress],
            ignoreOrder: true);

        busy.ShouldHaveSingleItem().Interval.Start.ShouldBe(BusyStart);
    }

    /// <summary>
    /// The upgrade path, and the reason this ships fixed rather than fixable: a deployment that
    /// has never opened the panel reads every calendar Google itself shows as selected. The
    /// secondary is <c>selected:false</c> in the fixture, so it is deliberately not among them.
    /// </summary>
    [Fact]
    public async Task GetBusyAsync_WithNothingSaved_UsesEveryCalendarGoogleMarksSelected()
    {
        var handler = new FakeGoogleHandler()
            .Answering(CalendarListPath, CalendarList)
            .Answering(FreebusyPath, OneBusyPeriod);

        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);

        var busy = await harness.Adapter.GetBusyAsync(
            Window, TestContext.Current.CancellationToken);

        AskedFor(LastFreebusy(handler)).ShouldBe(
            [PrimaryCalendarAddress, SubscribedCalendarAddress],
            ignoreOrder: true);

        busy.ShouldHaveSingleItem().Interval.Start.ShouldBe(BusyStart);
    }

    /// <summary>
    /// Unticking everything is a decision, not a blank. It says Google contributes no busy time,
    /// and it has to survive every load after the one that saved it — keying the default off an
    /// empty list rather than an absent key read the decision as "never saved", worked the default
    /// out again and put every calendar the operator had just removed straight back.
    /// </summary>
    /// <remarks>
    /// Nothing is stubbed, because nothing should be asked: no calendar list, since no default is
    /// being worked out, and no freebusy, since a query naming no calendar has no answer to give.
    /// <c>UnstubbedUris</c> is what makes that assertion mean something — <c>GetBusyAsync</c>
    /// swallows a provider failure, so a call that did go out would come back as the same empty
    /// list a correct one does.
    /// </remarks>
    [Fact]
    public async Task GetBusyAsync_WithAnEmptySelectionSaved_AsksGoogleNothing()
    {
        var handler = new FakeGoogleHandler();
        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);
        await harness.SaveBusyCalendarsAsync();

        for (var load = 0; load < PageLoads; load++)
        {
            var busy = await harness.Adapter.GetBusyAsync(
                Window, TestContext.Current.CancellationToken);

            busy.ShouldBeEmpty();
        }

        handler.Requests.ShouldBeEmpty();
        handler.UnstubbedUris.ShouldBeEmpty();
    }

    /// <summary>
    /// Once, not per page load. <c>GetBusyAsync</c> runs on every view of an anonymous public
    /// page, so recomputing the default each time would pay a second Google round trip forever.
    /// The second query asking for the same calendars is what proves the first was written down.
    /// </summary>
    [Fact]
    public async Task GetBusyAsync_CalledTwiceWithNothingSaved_AsksForTheCalendarListOnlyOnce()
    {
        var handler = new FakeGoogleHandler()
            .Answering(CalendarListPath, CalendarList)
            .Answering(FreebusyPath, OneBusyPeriod);

        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);

        for (var load = 0; load < PageLoads; load++)
        {
            var busy = await harness.Adapter.GetBusyAsync(
                Window, TestContext.Current.CancellationToken);

            busy.ShouldHaveSingleItem().Interval.Start.ShouldBe(BusyStart);
        }

        handler.Requests.Count(request => request.Path.Contains(CalendarListPath))
            .ShouldBe(CalendarListCalls);

        var asked = handler.Requests
            .Where(request => request.Path.Contains(FreebusyPath))
            .Select(request => AskedFor(request.Body))
            .ToList();

        asked.Count.ShouldBe(PageLoads);

        foreach (var calendars in asked)
        {
            calendars.ShouldBe(
                [PrimaryCalendarAddress, SubscribedCalendarAddress],
                ignoreOrder: true);
        }
    }

    /// <summary>
    /// Google would not say which calendars exist, so nothing is written down on a guess and the
    /// next load asks again. The account's own calendar is the one id that is always right, and
    /// falling back to it is exactly what every deployment did before this setting existed.
    /// </summary>
    [Fact]
    public async Task GetBusyAsync_WithNothingSavedAndNoCalendarList_AsksAboutThePrimaryAlone()
    {
        // Freebusy is canned and the calendar list is not, so the handler throws on the list and
        // answers the query — which is what a half-reachable Google looks like from in here.
        var handler = new FakeGoogleHandler().Answering(FreebusyPath, OneBusyPeriod);
        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);

        var busy = await harness.Adapter.GetBusyAsync(
            Window, TestContext.Current.CancellationToken);

        AskedFor(LastFreebusy(handler)).ShouldHaveSingleItem().ShouldBe(PrimaryCalendarAlias);
        busy.ShouldHaveSingleItem().Interval.Start.ShouldBe(BusyStart);
    }

    /// <summary>
    /// Google shows nothing ticked, which is a state of the account rather than an answer about
    /// it. A freebusy query naming no calendar reports no busy time at all, so every slot in the
    /// week would be offered — the account's own calendar is the floor under that, under the
    /// address Google lists it by rather than the alias, so the settings panel can match it to an
    /// option it offers.
    /// </summary>
    [Fact]
    public async Task GetBusyAsync_WithNoCalendarMarkedSelected_AsksAboutTheAccountsOwnCalendarAlone()
    {
        var handler = new FakeGoogleHandler()
            .Answering(CalendarListPath, NothingSelectedCalendarList)
            .Answering(FreebusyPath, OneBusyPeriod);

        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);

        var busy = await harness.Adapter.GetBusyAsync(
            Window, TestContext.Current.CancellationToken);

        AskedFor(LastFreebusy(handler)).ShouldHaveSingleItem().ShouldBe(PrimaryCalendarAddress);
        busy.ShouldHaveSingleItem().Interval.Start.ShouldBe(BusyStart);
    }

    /// <summary>
    /// The floor is written down like any other default. Nothing ticked in Google is a state that
    /// persists, so a fallback that answered without recording itself asked for the calendar list
    /// on every view of the booking page — the one round trip per load the materialisation exists
    /// to stop.
    /// </summary>
    [Fact]
    public async Task GetBusyAsync_CalledTwiceWithNoCalendarMarkedSelected_AsksForTheCalendarListOnlyOnce()
    {
        var handler = new FakeGoogleHandler()
            .Answering(CalendarListPath, NothingSelectedCalendarList)
            .Answering(FreebusyPath, OneBusyPeriod);

        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);

        for (var load = 0; load < PageLoads; load++)
        {
            var busy = await harness.Adapter.GetBusyAsync(
                Window, TestContext.Current.CancellationToken);

            busy.ShouldHaveSingleItem().Interval.Start.ShouldBe(BusyStart);
        }

        handler.Requests.Count(request => request.Path.Contains(CalendarListPath))
            .ShouldBe(CalendarListCalls);

        AskedFor(LastFreebusy(handler)).ShouldHaveSingleItem().ShouldBe(PrimaryCalendarAddress);
    }

    /// <summary>
    /// Google refuses a freebusy query naming more calendars than it allows, and a refused query
    /// reads to availability as an organizer free all week. The cap is applied where the default
    /// is worked out and written down, so the panel afterwards shows what Kairos is really using.
    /// </summary>
    [Fact]
    public async Task GetBusyAsync_WithMoreCalendarsSelectedThanFreebusyAccepts_AsksAboutTheCap()
    {
        var handler = new FakeGoogleHandler()
            .Answering(CalendarListPath, OverCapCalendarList)
            .Answering(FreebusyPath, OneBusyPeriod);

        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);

        var busy = await harness.Adapter.GetBusyAsync(
            Window, TestContext.Current.CancellationToken);

        AskedFor(LastFreebusy(handler)).Count
            .ShouldBe(GoogleCalendarAdapter.MaxFreebusyCalendars);

        busy.ShouldHaveSingleItem().Interval.Start.ShouldBe(BusyStart);
    }

    /// <summary>
    /// The other half of the same defect. Reading five calendars and writing to one the operator
    /// did not choose is a meeting that lands where nobody is looking for it.
    /// </summary>
    [Fact]
    public async Task CreateEventAsync_WithAChosenCalendar_InsertsIntoIt()
    {
        var handler = new FakeGoogleHandler().Answering(EventsPath, InsertedEvent);
        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);
        await harness.SaveWriteCalendarAsync(SecondaryCalendarAddress);

        var placed = await harness.Adapter.CreateEventAsync(
            harness.SampleRequest(BookingId), TestContext.Current.CancellationToken);

        LastEventPath(handler).ShouldContain(SecondaryCalendarInPath);
        placed.ExternalEventId.ShouldBe(ExternalEventId);
        handler.UnstubbedUris.ShouldBeEmpty();
    }

    /// <summary>
    /// Nothing chosen is the state of every deployment before the panel existed, and it must keep
    /// meaning what it has always meant. A booking that stopped arriving because a setting was
    /// added would be the worst thing this change could do.
    /// </summary>
    [Fact]
    public async Task CreateEventAsync_WithNothingChosen_InsertsIntoThePrimaryCalendar()
    {
        // The calendar list is deliberately unstubbed: working out where to write must not depend
        // on Google being reachable, or a booking would stop arriving during an outage.
        var handler = new FakeGoogleHandler().Answering(EventsPath, InsertedEvent);
        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);

        var placed = await harness.Adapter.CreateEventAsync(
            harness.SampleRequest(BookingId), TestContext.Current.CancellationToken);

        LastEventPath(handler).ShouldContain(PrimaryCalendarAlias);
        placed.ExternalEventId.ShouldBe(ExternalEventId);
        handler.UnstubbedUris.ShouldBeEmpty();
    }

    /// <summary>
    /// A reschedule has to find the event where the insert left it. Patching the primary calendar
    /// for an event that lives elsewhere is a 404, and a booking stuck at its old time.
    /// </summary>
    [Fact]
    public async Task UpdateEventAsync_WithAChosenCalendar_MovesTheEventInIt()
    {
        var handler = new FakeGoogleHandler().Answering(EventsPath, InsertedEvent);
        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);
        await harness.SaveWriteCalendarAsync(SecondaryCalendarAddress);

        await harness.Adapter.UpdateEventAsync(
            ExternalEventId,
            harness.SampleRequest(BookingId),
            TestContext.Current.CancellationToken);

        LastEventPath(handler).ShouldContain(SecondaryCalendarInPath);
        handler.UnstubbedUris.ShouldBeEmpty();
    }

    /// <summary>
    /// The same again for the withdrawal, and it fails worse: a delete aimed at the wrong calendar
    /// reports success against nothing while the meeting stays in the operator's day.
    /// </summary>
    [Fact]
    public async Task CancelEventAsync_WithAChosenCalendar_WithdrawsTheEventFromIt()
    {
        var handler = new FakeGoogleHandler().Answering(EventsPath, NoBody);
        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);
        await harness.SaveWriteCalendarAsync(SecondaryCalendarAddress);

        await harness.Adapter.CancelEventAsync(
            ExternalEventId, TestContext.Current.CancellationToken);

        LastEventPath(handler).ShouldContain(SecondaryCalendarInPath);
        handler.UnstubbedUris.ShouldBeEmpty();
    }

    /// <summary>
    /// The conference is read back off the event the insert created, so it is read out of the same
    /// calendar the insert wrote to — otherwise every booking is confirmed with no join link.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WithAChosenCalendar_ReadsTheConferenceBackFromIt()
    {
        var handler = new FakeGoogleHandler().Answering(EventsPath, EventWithConference);
        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);
        await harness.SaveWriteCalendarAsync(SecondaryCalendarAddress);

        var conference = await harness.Adapter.CreateAsync(
            BookingId, TestContext.Current.CancellationToken);

        LastEventPath(handler).ShouldContain(SecondaryCalendarInPath);
        conference.ExternalId.ShouldBe(ConferenceId);
        conference.JoinUrl.ShouldBe(new Uri(JoinUrl));
        handler.UnstubbedUris.ShouldBeEmpty();
    }

    /// <summary>
    /// Everything the operator can pick from, not everything they can write to. A subscription is
    /// legitimately readable and hopeless to insert into, and the write field filters it out on
    /// its own.
    /// </summary>
    [Fact]
    public async Task DescribeSettingsAsync_WithAnOwnedAndASubscribedCalendar_OffersOnlyTheOwnedOnesToWriteTo()
    {
        var handler = new FakeGoogleHandler().Answering(CalendarListPath, CalendarList);
        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);

        var fields = await harness.Adapter.DescribeSettingsAsync(
            TestContext.Current.CancellationToken);

        var busy = fields.Single(field => field.Key == GoogleCalendarAdapter.BusyCalendarsKey);
        busy.Kind.ShouldBe(SettingFieldKind.MultiSelect);
        busy.Options.ShouldNotBeNull();
        busy.Options.Select(option => option.Value).ShouldBe(
            [PrimaryCalendarAddress, SecondaryCalendarAddress, SubscribedCalendarAddress],
            ignoreOrder: true);

        var write = fields.Single(field => field.Key == GoogleCalendarAdapter.WriteCalendarKey);
        write.Kind.ShouldBe(SettingFieldKind.Select);
        write.Required.ShouldBeTrue();
        write.Options.ShouldNotBeNull();
        write.Options.Select(option => option.Value).ShouldBe(
            [PrimaryCalendarAddress, SecondaryCalendarAddress],
            ignoreOrder: true);
    }

    /// <summary>
    /// Unshared, deleted, or renamed out from under the operator. It stays ticked and stays
    /// visible, because silently dropping it would quietly widen what Kairos offers — and the
    /// operator would learn about it from a double booking.
    /// </summary>
    [Fact]
    public async Task DescribeSettingsAsync_WithASavedCalendarGoogleNoLongerLists_KeepsItSelectedAndSaysWhy()
    {
        var handler = new FakeGoogleHandler().Answering(CalendarListPath, CalendarList);
        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);
        await harness.SaveBusyCalendarsAsync(PrimaryCalendarAddress, DepartedCalendarAddress);

        var fields = await harness.Adapter.DescribeSettingsAsync(
            TestContext.Current.CancellationToken);

        var busy = fields.Single(field => field.Key == GoogleCalendarAdapter.BusyCalendarsKey);
        busy.Options.ShouldNotBeNull();

        var departed = busy.Options.Single(option => option.Value == DepartedCalendarAddress);
        departed.Detail.ShouldNotBeNullOrEmpty();

        busy.Value.EnumerateArray().Select(value => value.GetString())
            .ShouldContain(DepartedCalendarAddress);
    }

    /// <summary>
    /// The same for the calendar bookings are written to, whose options were the writable list
    /// raw. A saved value no option carries renders the select blank and comes back
    /// <c>setting_invalid</c> on the next save, over a choice the operator was never shown.
    /// </summary>
    [Fact]
    public async Task DescribeSettingsAsync_WithASavedWriteCalendarGoogleNoLongerLists_StillOffersIt()
    {
        var handler = new FakeGoogleHandler().Answering(CalendarListPath, CalendarList);
        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);
        await harness.SaveWriteCalendarAsync(DepartedCalendarAddress);

        var fields = await harness.Adapter.DescribeSettingsAsync(
            TestContext.Current.CancellationToken);

        var write = fields.Single(field => field.Key == GoogleCalendarAdapter.WriteCalendarKey);
        write.Options.ShouldNotBeNull();

        var departed = write.Options.Single(option => option.Value == DepartedCalendarAddress);
        departed.Detail.ShouldNotBeNullOrEmpty();

        write.Value.GetString().ShouldBe(DepartedCalendarAddress);
        handler.UnstubbedUris.ShouldBeEmpty();
    }

    /// <summary>
    /// Null options mean the provider could not be reached, never "there are none". The saved set
    /// is still what Kairos is using, and it is still what the panel must show.
    /// </summary>
    [Fact]
    public async Task DescribeSettingsAsync_WhenGoogleCannotBeReached_KeepsTheSavedValueAndOffersNoOptions()
    {
        // No canned calendar list, so the handler throws — which is what an outage looks like from
        // in here.
        var handler = new FakeGoogleHandler();
        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);
        await harness.SaveBusyCalendarsAsync(PrimaryCalendarAddress);

        var fields = await harness.Adapter.DescribeSettingsAsync(
            TestContext.Current.CancellationToken);

        var busy = fields.Single(field => field.Key == GoogleCalendarAdapter.BusyCalendarsKey);
        busy.Options.ShouldBeNull();
        busy.Value.EnumerateArray().Select(value => value.GetString())
            .ShouldBe([PrimaryCalendarAddress]);

        // The outage is the calendar list and only the calendar list. DescribeSettingsAsync
        // swallows a provider failure by design, so without this a call that drifted onto some
        // other unstubbed path would fail here too and read as the same passing test.
        handler.UnstubbedUris.ShouldHaveSingleItem().ShouldContain(CalendarListPath);
    }

    /// <summary>
    /// The cap is Google's, and it is enforced where the operator can act on it. Truncating at read
    /// time would drop calendars months later, silently, and offer slots for meetings it stopped
    /// looking at.
    /// </summary>
    [Fact]
    public async Task SaveSettingsAsync_WithMoreCalendarsThanFreebusyAccepts_IsRefused()
    {
        var handler = new FakeGoogleHandler().Answering(CalendarListPath, CalendarList);
        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);

        var tooMany = Enumerable
            .Range(0, GoogleCalendarAdapter.MaxFreebusyCalendars + 1)
            .Select(index => $"calendar-{index}@example.com")
            .ToArray();

        var failure = await Should.ThrowAsync<DomainException>(
            () => harness.Adapter.SaveSettingsAsync(
                Values(GoogleCalendarAdapter.BusyCalendarsKey, tooMany),
                TestContext.Current.CancellationToken));

        failure.Code.ShouldBe(IIntegrationSettings.SettingTooManyCode);
    }

    /// <summary>
    /// A value that is not on the list is a client that has drifted or a calendar that went away
    /// mid-edit. Either way it is refused, so a save cannot invent a calendar.
    /// </summary>
    [Fact]
    public async Task SaveSettingsAsync_WithACalendarGoogleDoesNotOffer_IsRefused()
    {
        var handler = new FakeGoogleHandler().Answering(CalendarListPath, CalendarList);
        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);

        var failure = await Should.ThrowAsync<DomainException>(
            () => harness.Adapter.SaveSettingsAsync(
                Values(GoogleCalendarAdapter.BusyCalendarsKey, [DepartedCalendarAddress]),
                TestContext.Current.CancellationToken));

        failure.Code.ShouldBe(IIntegrationSettings.SettingInvalidCode);
    }

    /// <summary>
    /// A key nobody describes is refused without asking Google anything: the account's calendars
    /// have no bearing on a field that does not exist, and a provider that had to be reachable to
    /// reject a typo would turn an outage into a 400 the operator cannot read.
    /// </summary>
    [Fact]
    public async Task SaveSettingsAsync_WithAKeyGoogleDoesNotDescribe_IsRefusedWithoutAskingGoogle()
    {
        var handler = new FakeGoogleHandler();
        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);

        var failure = await Should.ThrowAsync<DomainException>(
            () => harness.Adapter.SaveSettingsAsync(
                Values(UnknownKey, [PrimaryCalendarAddress]),
                TestContext.Current.CancellationToken));

        failure.Code.ShouldBe(IIntegrationSettings.SettingUnknownCode);
        handler.Requests.ShouldBeEmpty();
    }

    /// <summary><paramref name="count"/> owned calendars, all of them ticked in Google.</summary>
    private static string SelectedCalendarList(int count)
    {
        var entries = Enumerable.Range(0, count).Select(index =>
            $$"""{"id":"calendar-{{index}}@example.com","selected":true,"accessRole":"owner"}""");

        return $$"""{"items":[{{string.Join(",", entries)}}]}""";
    }

    /// <summary>The calendar ids one freebusy request body names.</summary>
    private static List<string?> AskedFor(string body) =>
        [.. JsonDocument.Parse(body).RootElement
            .GetProperty(ItemsProperty)
            .EnumerateArray()
            .Select(item => item.GetProperty(IdProperty).GetString())];

    /// <summary>
    /// The freebusy body, picked out of whatever else the adapter asked Google on the way to it.
    /// </summary>
    private static string LastFreebusy(FakeGoogleHandler handler) =>
        handler.Requests.Last(request => request.Path.Contains(FreebusyPath)).Body;

    /// <summary>The path of the last event call, which is the one naming the calendar written to.</summary>
    private static string LastEventPath(FakeGoogleHandler handler) =>
        handler.Requests.Last(request => request.Path.Contains(EventsPath)).Path;

    private static Dictionary<string, JsonElement> Values(string key, string[] values) =>
        new() { [key] = JsonSerializer.SerializeToElement(values) };
}
