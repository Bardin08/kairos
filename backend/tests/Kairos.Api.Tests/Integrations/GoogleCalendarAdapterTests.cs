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

    /// <summary>Google's alias for the signed-in account's own calendar.</summary>
    private const string PrimaryCalendarAlias = "primary";

    private const string ItemsProperty = "items";

    private const string IdProperty = "id";

    /// <summary>The connected account's own calendar, which Google lists by its address.</summary>
    private const string PrimaryCalendarAddress = "operator@example.com";

    private const string SecondaryCalendarAddress = "side@example.com";

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
    /// week would be offered — the account's own calendar is the floor under that.
    /// </summary>
    [Fact]
    public async Task GetBusyAsync_WithNoCalendarMarkedSelected_AsksAboutThePrimaryAlone()
    {
        var handler = new FakeGoogleHandler()
            .Answering(CalendarListPath, NothingSelectedCalendarList)
            .Answering(FreebusyPath, OneBusyPeriod);

        await using var harness = await GoogleAdapterHarness.CreateAsync(handler);

        var busy = await harness.Adapter.GetBusyAsync(
            Window, TestContext.Current.CancellationToken);

        AskedFor(LastFreebusy(handler)).ShouldHaveSingleItem().ShouldBe(PrimaryCalendarAlias);
        busy.ShouldHaveSingleItem().Interval.Start.ShouldBe(BusyStart);
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

    private static Dictionary<string, JsonElement> Values(string key, string[] values) =>
        new() { [key] = JsonSerializer.SerializeToElement(values) };
}
