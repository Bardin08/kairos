using System.Text.Json;

using Kairos.Api.Integrations;
using Kairos.Api.Integrations.Google;
using Kairos.Api.Scheduling;
using Kairos.Api.Shared;

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

    private static Dictionary<string, JsonElement> Values(string key, string[] values) =>
        new() { [key] = JsonSerializer.SerializeToElement(values) };
}
