using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Kairos.Api.Features.Bookings;
using Kairos.Api.Features.Slots;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Shared;

using Microsoft.EntityFrameworkCore;

using NodaTime;
using NodaTime.Text;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The HTTP surface over the real pipeline. What is pinned here is the wiring — which route
/// answers, which failure becomes which status, and the two rules that live at this layer and
/// nowhere below it: a booking is re-checked against generated slots before it is written, and a
/// reschedule moves the meeting rather than replacing it.
/// </summary>
public class EndpointTests
{
    private const string Slug = "consulting";
    private const int DurationMinutes = 25;
    private const int NineAm = 9 * 60;
    private const int FivePm = 17 * 60;
    private const string FirstSlotTime = "T09:00:00Z";
    private const string ThirdSlotTime = "T10:00:00Z";
    private const string NeverOfferedTime = "T03:00:00Z";

    /// <summary>Days ahead of the seeding, so real-clock minimum notice never decides a test.</summary>
    private const int WellAhead = 30;

    private static readonly Guid PatternId = Guid.CreateVersion7();
    private static readonly Guid LinkId = Guid.CreateVersion7();

    [Fact]
    public async Task GetSlots_UnderTheVersionPrefix_ReturnsTheDaysInstants()
    {
        using var app = NewApp();
        var day = Day();

        var slots = await app.Client.GetFromJsonAsync<string[]>(
            SlotsUrl(day, day), TestContext.Current.CancellationToken);

        slots.ShouldNotBeNull();
        slots.ShouldContain(day + FirstSlotTime);
        slots.ShouldNotContain(day + NeverOfferedTime);
    }

    /// <summary>
    /// 30 February parses as a date shape and is not a date. That is why the parse, not a regular
    /// expression, is what decides — and why it has to answer 400 rather than fall through to a
    /// 500 from somewhere further down.
    /// </summary>
    [Fact]
    public async Task GetSlots_MalformedDate_IsABadRequestNamingTheCode()
    {
        using var app = NewApp();

        var response = await app.Client.GetAsync(
            SlotsUrl("2026-02-30", "2026-02-30"), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOfAsync(response)).ShouldBe(SlotsEndpoints.DateInvalidCode);
    }

    /// <summary>
    /// An inverted range answers empty inside SlotQuery, which the booking page would render as
    /// "no times available" — a real answer, for a request that is not one.
    /// </summary>
    [Fact]
    public async Task GetSlots_ToBeforeFrom_IsABadRequestRatherThanAnEmptyList()
    {
        using var app = NewApp();
        var day = Day();

        var response = await app.Client.GetAsync(
            SlotsUrl(day, LocalDatePattern.Iso.Format(LocalDatePattern.Iso.Parse(day).Value.PlusDays(-1))),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOfAsync(response)).ShouldBe(SlotsEndpoints.RangeInvertedCode);
    }

    /// <summary>
    /// The other half of the handler's contract, end to end: a stored zone nobody can resolve is
    /// a bug, and a bug stays a 500 with a stack trace instead of being dressed up as a 400.
    /// </summary>
    [Fact]
    public async Task GetSlots_UnexpectedFailure_StaysAServerError()
    {
        using var app = NewApp();
        app.Seed(context => context.Settings
            .Where(row => row.Id == Settings.SingletonId)
            .ExecuteUpdate(row => row.SetProperty(settings => settings.TimeZoneId, "Mars/Olympus")));

        var day = Day();
        var response = await app.Client.GetAsync(SlotsUrl(day, day), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    /// <summary>
    /// The writer's SQL only refuses a clash with another confirmed booking, so 03:00 on a day
    /// that opens at 09:00 would be written without complaint. Availability, notice and cap are
    /// enforced on write because this endpoint asks for the slots again first.
    /// </summary>
    [Fact]
    public async Task CreateBooking_InstantTheLinkNeverOffered_IsRefusedBeforeTheWriter()
    {
        using var app = NewApp();

        var response = await CreateAsync(app, Day() + NeverOfferedTime);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(response)).ShouldBe(BookingsEndpoints.SlotUnavailableCode);
        app.Read(context => context.Bookings.Count()).ShouldBe(0);
    }

    [Fact]
    public async Task RescheduleBooking_MovesTheMeeting_KeepsTheIdentifierAndIncrementsSequence()
    {
        using var app = NewApp();
        var day = Day();

        var created = await ReadJsonAsync(await CreateAsync(app, day + FirstSlotTime));
        var token = created.GetProperty("manageToken").GetString();

        var response = await app.Client.PostAsJsonAsync(
            $"{ApiVersions.V1}/bookings/{token}/reschedule",
            new { start = day + ThirdSlotTime },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var moved = await ReadJsonAsync(response);
        moved.GetProperty("id").GetString().ShouldBe(created.GetProperty("id").GetString());
        moved.GetProperty("start").GetString().ShouldBe(day + ThirdSlotTime);
        moved.GetProperty("sequence").GetInt32()
            .ShouldBe(created.GetProperty("sequence").GetInt32() + 1);
        app.Read(context => context.Bookings.Count()).ShouldBe(1);
    }

    [Fact]
    public async Task CancelBooking_TokenThatMatchesNothing_IsNotFound()
    {
        using var app = NewApp();
        await CreateAsync(app, Day() + FirstSlotTime);

        var response = await app.Client.PostAsync(
            $"{ApiVersions.V1}/bookings/not-a-token/cancel", null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        app.Read(context => context.Bookings.Count(booking => booking.Status == BookingStatus.Confirmed))
            .ShouldBe(1);
    }

    private static KairosApp NewApp()
    {
        var app = new KairosApp();
        app.Seed(Seed);
        return app;
    }

    private static void Seed(KairosDbContext context)
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
            Name = "Consulting call",
            PatternId = PatternId,
            Active = true,
        });
        context.LinkDurations.Add(new LinkDuration { LinkId = LinkId, Minutes = DurationMinutes, SortOrder = 0 });
    }

    /// <summary>
    /// Computed rather than written down. The application reads the real clock, so a literal date
    /// would turn these tests into a suite that passes until it silently stops.
    /// </summary>
    private static string Day() => LocalDatePattern.Iso.Format(
        SystemClock.Instance.GetCurrentInstant().InUtc().Date.PlusDays(WellAhead).Next(IsoDayOfWeek.Monday));

    private static string SlotsUrl(string from, string to) =>
        $"{ApiVersions.V1}/links/{Slug}/slots?from={from}&to={to}&duration={DurationMinutes}";

    private static Task<HttpResponseMessage> CreateAsync(KairosApp app, string start) =>
        app.Client.PostAsJsonAsync(
            $"{ApiVersions.V1}/bookings",
            new
            {
                slug = Slug,
                start,
                durationMinutes = DurationMinutes,
                name = "Vlad",
                email = "vlad@example.com",
                timeZone = "Europe/Warsaw",
            },
            TestContext.Current.CancellationToken);

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).GetProperty("code").GetString();

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}
