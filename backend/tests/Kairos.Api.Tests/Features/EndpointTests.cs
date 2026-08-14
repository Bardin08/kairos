using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Kairos.Api.Features.Bookings;
using Kairos.Api.Features.Slots;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Scheduling;
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

    /// <summary>A slug no link can have, so both link routes must answer the same way.</summary>
    private const string UnusableSlug = "ABC_%21";

    private const int DurationMinutes = 25;
    private const int LongDurationMinutes = 50;
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

    /// <summary>
    /// The branch the release-then-re-check design exists to justify. A reschedule that fails
    /// must undo the release it made to run the check — if it does not, the caller sees an error
    /// and believes their meeting stands while the row sits cancelled in the database. Silent,
    /// destructive, and invisible to a test that only reads the status code.
    /// </summary>
    [Fact]
    public async Task RescheduleBooking_InstantTheLinkNeverOffered_LeavesTheRowConfirmedWhereItWas()
    {
        using var app = NewApp();
        var day = Day();

        var created = await ReadJsonAsync(await CreateAsync(app, day + FirstSlotTime));
        var token = created.GetProperty("manageToken").GetString();

        var response = await app.Client.PostAsJsonAsync(
            $"{ApiVersions.V1}/bookings/{token}/reschedule",
            new { start = day + NeverOfferedTime },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(response)).ShouldBe(BookingsEndpoints.SlotUnavailableCode);

        var row = app.Read(context => context.Bookings.AsNoTracking().Single());
        row.Status.ShouldBe(BookingStatus.Confirmed);
        row.CancelledAt.ShouldBeNull();
        row.Sequence.ShouldBe(created.GetProperty("sequence").GetInt32());
        InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(row.StartUtc))
            .ShouldBe(day + FirstSlotTime);
    }

    /// <summary>
    /// The second of the two 400 shapes, and the only thing that would notice AddValidation
    /// quietly ceasing to run: nothing below this layer looks at an address, so an unvalidated
    /// one is written to the database and answered 201.
    /// </summary>
    [Fact]
    public async Task CreateBooking_ViolatesAnAttribute_IsABadRequestWithErrorsAndNoCode()
    {
        using var app = NewApp();

        var response = await app.Client.PostAsJsonAsync(
            $"{ApiVersions.V1}/bookings",
            new
            {
                slug = Slug,
                start = Day() + FirstSlotTime,
                durationMinutes = DurationMinutes,
                name = "Vlad",
                email = "not-an-address",
                timeZone = "Europe/Warsaw",
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var body = await ReadJsonAsync(response);
        body.TryGetProperty("errors", out var errors).ShouldBeTrue();
        errors.TryGetProperty(nameof(CreateBookingRequest.Email), out _).ShouldBeTrue();
        body.TryGetProperty("code", out _).ShouldBeFalse();
        app.Read(context => context.Bookings.Count()).ShouldBe(0);
    }

    /// <summary>
    /// What the manage page loads, and the one guarantee the whole token scheme rests on: the
    /// plaintext is handed over exactly once, by create. Only its hash is stored, so a read that
    /// returned it again would mean it had been kept somewhere it should not be.
    /// </summary>
    [Fact]
    public async Task GetBooking_ValidToken_ReturnsTheBookingAndNeverTheTokenAgain()
    {
        using var app = NewApp();
        var day = Day();

        var created = await ReadJsonAsync(await CreateAsync(app, day + FirstSlotTime));
        var token = created.GetProperty("manageToken").GetString();

        var response = await app.Client.GetAsync(
            $"{ApiVersions.V1}/bookings/{token}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var read = await ReadJsonAsync(response);
        read.GetProperty("id").GetString().ShouldBe(created.GetProperty("id").GetString());
        read.GetProperty("start").GetString().ShouldBe(day + FirstSlotTime);
        read.GetProperty("manageToken").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>
    /// The booking page reads both of these off the link: the durations in the order the
    /// organizer chose, and the zone every instant in this API is derived from.
    /// </summary>
    [Fact]
    public async Task GetLink_ReturnsTheOrganizersZoneAndTheDurationsInTheirOwnOrder()
    {
        using var app = NewApp();

        var link = await app.Client.GetFromJsonAsync<JsonElement>(
            $"{ApiVersions.V1}/links/{Slug}", TestContext.Current.CancellationToken);

        link.GetProperty("timeZone").GetString().ShouldBe(SchedulingDefaults.TimeZoneId);
        link.GetProperty("durations").EnumerateArray().Select(duration => duration.GetInt32()).ToArray()
            .ShouldBe([LongDurationMinutes, DurationMinutes]);
    }

    /// <summary>
    /// Both link routes take the same slug and must answer it the same way. They did not: a
    /// pattern attribute on the slots request made an unusable slug a 400 there and a 404 here.
    /// The resolver's answer is the right one, so the attribute went.
    /// </summary>
    [Fact]
    public async Task UnusableSlug_IsNotFoundOnBothLinkRoutes()
    {
        using var app = NewApp();
        var day = Day();

        var detail = await app.Client.GetAsync(
            $"{ApiVersions.V1}/links/{UnusableSlug}", TestContext.Current.CancellationToken);
        var slots = await app.Client.GetAsync(
            $"{ApiVersions.V1}/links/{UnusableSlug}/slots?from={day}&to={day}&duration={DurationMinutes}",
            TestContext.Current.CancellationToken);

        detail.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        slots.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOfAsync(detail)).ShouldBe(DomainException.NotFoundCode);
        (await CodeOfAsync(slots)).ShouldBe(DomainException.NotFoundCode);
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
        // Listed longest first, so sort order and ascending length disagree. A booking page that
        // reorders them is showing the organizer a list they did not choose.
        context.LinkDurations.Add(new LinkDuration { LinkId = LinkId, Minutes = LongDurationMinutes, SortOrder = 0 });
        context.LinkDurations.Add(new LinkDuration { LinkId = LinkId, Minutes = DurationMinutes, SortOrder = 1 });
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
