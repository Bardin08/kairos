using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Kairos.Api.Features.Admin;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Scheduling;
using Kairos.Api.Shared;

using Microsoft.EntityFrameworkCore;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The organizer's zone, every global the screen writes, and the one promise it makes out loud:
/// changing the zone moves the wall clock availability is described in and moves no booking.
/// </summary>
public class SettingsAdminTests
{
    private const string SettingsPath = $"{ApiVersions.V1}/admin/settings";
    private const string ZonesPath = $"{SettingsPath}/zones";

    private const string Warsaw = "Europe/Warsaw";
    private const string Utc = "UTC";
    private const string UtcOffsetLabel = "+00:00";

    /// <summary>Deliberately not a zone. The TZDB is what says so, not a regular expression.</summary>
    private const string NotAZone = "Middle/Earth";

    private const int NewMargin = 20;
    private const int NewMinNotice = 90;
    private const int NewDailyCap = 6;
    private const int NewStartIncrement = 10;
    private const int NewProposalHold = 12;

    /// <summary>Not a shorter hold. A proposal that lapsed before it was sent holds nothing.</summary>
    private const int NoHold = 0;

    /// <summary>2030-09-09T11:00:00Z, so no real clock decides anything here.</summary>
    private const long SeededStartUtc = 1_915_614_000;
    private const int HalfAnHour = 30 * 60;

    /// <summary>
    /// A Monday in July 2030, and both halves matter: the pattern's only rule is a Monday, and
    /// Warsaw is on summer time then, so the zone under test is +02:00 rather than +01:00.
    /// </summary>
    private const string SummerMonday = "2030-07-01";

    private const string Slug = "inherits";
    private const string OverridingSlug = "overrides";
    private const int DurationMinutes = 30;
    private const int Monday = 1;
    private const int NineAm = 9 * 60;
    private const int FivePm = 17 * 60;
    private const int HalfHourly = 30;
    private const int Hourly = 60;

    private static readonly Guid PatternId = Guid.CreateVersion7();

    [Fact]
    public async Task ReadSettings_WithoutASession_IsUnauthorizedRatherThanAnswering()
    {
        using var app = new KairosApp();

        var response = await app.Client.GetAsync(SettingsPath, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The number the screen is built around. An operator changing a global has to know how many
    /// links will ignore it, and "three of them already say something else" is the difference
    /// between a change that lands and a change that appears to do nothing.
    /// </summary>
    [Fact]
    public async Task ReadSettings_CountsTheLinksThatOverrideEachLimit_AndNotTheOnesThatInherit()
    {
        using var app = new KairosApp();
        Seed(app, link => link.MarginMinutes = 5, link => link.DailyCap = 0, _ => { });
        using var client = app.CreateOperatorClient();

        var settings = await ReadAsync(client);

        settings.MarginMinutes.ShouldBe(SchedulingDefaults.MarginMinutes);
        settings.Overrides.MarginMinutes.ShouldBe(1);
        // Zero is a value, not an absence. A count that treated it as "no override" is exactly the
        // defect the nullable columns exist to prevent, one screen further on.
        settings.Overrides.DailyCap.ShouldBe(1);
        settings.Overrides.MinNoticeMinutes.ShouldBe(0);
        settings.Overrides.StartIncrementMinutes.ShouldBe(0);
    }

    /// <summary>
    /// Every value the operator sets, written and read back in one round trip — the four limits
    /// and the proposal hold, which the same response carries and the override counts do not.
    /// </summary>
    [Fact]
    public async Task SaveSettings_WritesEverySetting_AndTheyComeBackOnTheRead()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();

        await client.PutAsJsonAsync(SettingsPath, Body(Warsaw), TestContext.Current.CancellationToken);

        var settings = await ReadAsync(client);
        var payload = await client.GetFromJsonAsync<JsonElement>(
            SettingsPath, TestContext.Current.CancellationToken);

        settings.MarginMinutes.ShouldBe(NewMargin);
        settings.MinNoticeMinutes.ShouldBe(NewMinNotice);
        settings.DailyCap.ShouldBe(NewDailyCap);
        settings.StartIncrementMinutes.ShouldBe(NewStartIncrement);
        settings.ProposalHoldHours.ShouldBe(NewProposalHold);
        // The hold is a global like the four above it and not a limit a link may override, so it
        // rides on this response with no count beside it. A fifth entry here would mean the link
        // editor had grown a control for something the schema does not model.
        payload.GetProperty("overrides").TryGetProperty("proposalHoldHours", out _).ShouldBeFalse();
    }

    /// <summary>
    /// A zone nothing can read is not a validation nicety: the scheduler resolves the id on every
    /// availability query, so an unknown one takes the public booking page down and does it hours
    /// after the setting was saved.
    /// </summary>
    [Fact]
    public async Task SaveSettings_WithAZoneTheTzdbDoesNotCarry_IsRefusedAndNothingIsStored()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();

        var response = await client.PutAsJsonAsync(
            SettingsPath, Body(NotAZone), TestContext.Current.CancellationToken);
        var problem = await response.Content.ReadFromJsonAsync<ProblemWithCode>(
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        problem!.Code.ShouldBe(SettingsAdminEndpoints.TimeZoneUnknownCode);
        (await ReadAsync(client)).TimeZone.ShouldBe(SchedulingDefaults.TimeZoneId);
    }

    /// <summary>
    /// The floor that is not merely tidy. A zero start increment makes the generator's minute step
    /// never advance, so the control that can send one is the control that eventually will.
    /// </summary>
    [Fact]
    public async Task SaveSettings_WithAZeroStartIncrement_IsRefused()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();

        var response = await client.PutAsJsonAsync(
            SettingsPath,
            Body(Utc) with { StartIncrementMinutes = 0 },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadAsync(client)).StartIncrementMinutes
            .ShouldBe(SchedulingDefaults.StartIncrementMinutes);
    }

    /// <summary>
    /// Zero is not a shorter hold, it is a proposal that lapses before the mail carrying it is
    /// composed. Refused at the endpoint, and the database's check constraint refuses it again for
    /// anything that gets past here.
    /// </summary>
    [Fact]
    public async Task SaveSettings_AZeroProposalHold_IsRefused()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();

        var saved = await client.PutAsJsonAsync(
            SettingsPath,
            Body(Utc) with { ProposalHoldHours = NoHold },
            TestContext.Current.CancellationToken);

        saved.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadAsync(client)).ProposalHoldHours.ShouldBe(SchedulingDefaults.ProposalHoldHours);
    }

    [Fact]
    public async Task SaveSettings_WithANegativeMargin_IsRefused()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();

        var response = await client.PutAsJsonAsync(
            SettingsPath,
            Body(Utc) with { MarginMinutes = -1 },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// The picker's options come from the same TZDB the write is checked against and the scheduler
    /// resolves with. A list built from anywhere else would offer a zone the save then refuses.
    /// </summary>
    [Fact]
    public async Task ListTimeZones_OffersTheTzdbIdsWithTheirCurrentOffsets()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();

        var zones = (await client.GetFromJsonAsync<List<TimeZoneOptionResponse>>(
            ZonesPath, TestContext.Current.CancellationToken)).ShouldNotBeNull();

        zones.ShouldContain(zone => zone.Id == Warsaw);
        // The label's shape, which the operator scans by: signed, padded, always both fields.
        zones.ShouldContain(zone => zone.Id == Utc && zone.Offset == UtcOffsetLabel);
    }

    /// <summary>
    /// The workbench draws no identity block without one, so what matters is that the claim
    /// reaches the response at all rather than which provider supplied it.
    /// </summary>
    [Fact]
    public async Task ReadSettings_CarriesTheOperatorsNameFromTheSignedInClaims()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();

        (await ReadAsync(client)).SignedInAs.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The zone change, end to end, as the plan asks for it to be verified.
    /// </summary>
    /// <remarks>
    /// Two claims in one test because they are one claim: the wall clock the availability rules
    /// describe moves, and nothing already booked moves with it. Split apart, either half passes
    /// while the other is broken — a zone that changed nothing at all satisfies the second, and a
    /// zone change that rewrote every start satisfies the first.
    /// </remarks>
    [Fact]
    public async Task SaveSettings_MovingTheZoneToWarsaw_MovesTheOfferedWindow_AndNoBooking()
    {
        using var app = new KairosApp();
        SeedBookable(app);
        using var client = app.CreateOperatorClient();

        var before = app.Read(context =>
            context.Bookings.AsNoTracking().Select(booking => booking.StartUtc).Single());
        var inUtc = await FirstSlotAsync(app);

        // Only the zone differs from what was seeded, so nothing else can be the reason the
        // window moved.
        await client.PutAsJsonAsync(
            SettingsPath,
            Seeded(SchedulingDefaults.StartIncrementMinutes) with { TimeZoneId = Warsaw },
            TestContext.Current.CancellationToken);

        var inWarsaw = await FirstSlotAsync(app);
        var after = app.Read(context =>
            context.Bookings.AsNoTracking().Select(booking => booking.StartUtc).Single());

        // 09:00 read in UTC, then 09:00 read in Warsaw — which is 07:00Z in July. The pattern was
        // never edited; the zone it is read in was.
        inUtc.ShouldBe($"{SummerMonday}T09:00:00Z");
        inWarsaw.ShouldBe($"{SummerMonday}T07:00:00Z");
        after.ShouldBe(before);
    }

    /// <summary>
    /// The other half of Step 4: a global reaches the links that inherit it and stops at the ones
    /// that do not. Observed through the slots the public page would offer, because that is where
    /// the setting has its effect — a database read would only prove the column was written.
    /// </summary>
    [Fact]
    public async Task SaveSettings_RaisingTheStartIncrement_MovesTheInheritingLink_AndNotTheOverriding()
    {
        using var app = new KairosApp();
        SeedBookable(app);
        using var client = app.CreateOperatorClient();

        // The override is set through Task 3's route rather than by writing the column, so what is
        // exercised is the same path the workbench takes.
        var overridden = app.Read(context => context.Links.AsNoTracking()
            .Where(link => link.Slug == OverridingSlug)
            .Select(link => link.Id)
            .Single());

        await client.PutAsJsonAsync(
            $"{ApiVersions.V1}/admin/links/{overridden}/limits",
            new { startIncrementMinutes = HalfHourly },
            TestContext.Current.CancellationToken);

        var inheritsBefore = await SlotCountAsync(app, Slug);
        var overridesBefore = await SlotCountAsync(app, OverridingSlug);

        // The increment and nothing else: a margin that moved at the same time would change both
        // links and prove neither.
        await client.PutAsJsonAsync(
            SettingsPath, Seeded(Hourly), TestContext.Current.CancellationToken);

        var inheritsAfter = await SlotCountAsync(app, Slug);
        var overridesAfter = await SlotCountAsync(app, OverridingSlug);

        // Both start half-hourly: one because the global says so, one because it says so itself.
        inheritsBefore.ShouldBe(overridesBefore);
        // The global moves to hourly. The link that inherits follows it and offers half as many
        // starts; the link that wrote its own ignores it entirely.
        inheritsAfter.ShouldBeLessThan(inheritsBefore);
        overridesAfter.ShouldBe(overridesBefore);
    }

    /// <summary>The first start the public page would offer on the seeded Monday.</summary>
    private static async Task<string> FirstSlotAsync(KairosApp app) =>
        (await SlotsAsync(app, Slug)).First();

    private static async Task<int> SlotCountAsync(KairosApp app, string slug) =>
        (await SlotsAsync(app, slug)).Count;

    private static async Task<List<string>> SlotsAsync(KairosApp app, string slug) =>
        (await app.Client.GetFromJsonAsync<List<string>>(
            $"{ApiVersions.V1}/links/{slug}/slots?from={SummerMonday}&to={SummerMonday}"
            + $"&duration={DurationMinutes}",
            TestContext.Current.CancellationToken)).ShouldNotBeNull();

    private static async Task<SettingsResponse> ReadAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<SettingsResponse>(
            SettingsPath, TestContext.Current.CancellationToken)).ShouldNotBeNull();

    /// <summary>Exactly what the database was seeded with, save for the one value under test.</summary>
    private static SaveSettingsRequest Seeded(int startIncrementMinutes) => new()
    {
        TimeZoneId = SchedulingDefaults.TimeZoneId,
        MarginMinutes = SchedulingDefaults.MarginMinutes,
        MinNoticeMinutes = SchedulingDefaults.MinNoticeMinutes,
        DailyCap = SchedulingDefaults.DailyCap,
        StartIncrementMinutes = startIncrementMinutes,
        ProposalHoldHours = SchedulingDefaults.ProposalHoldHours,
    };

    /// <summary>
    /// Two links on one Monday-only pattern, and one booking on a day neither test asks about.
    /// </summary>
    /// <remarks>
    /// The booking is deliberately elsewhere in the calendar: it exists so the zone change has
    /// something to leave alone, and putting it inside the queried day would make it a busy block
    /// silently shaping every slot count in the other test.
    /// </remarks>
    private static void SeedBookable(KairosApp app)
    {
        app.Seed(context =>
        {
            context.Patterns.Add(new Pattern
            {
                Id = PatternId,
                Name = "Mondays",
                Rules =
                [
                    new PatternRule
                    {
                        IsoDayOfWeek = Monday,
                        StartMinute = NineAm,
                        EndMinute = FivePm,
                    },
                ],
            });

            var inherits = Bookable(Slug);
            context.Links.AddRange(inherits, Bookable(OverridingSlug));

            context.Bookings.Add(new Booking
            {
                Link = inherits,
                StartUtc = SeededStartUtc,
                EndUtc = SeededStartUtc + HalfAnHour,
                Status = BookingStatus.Confirmed,
                ManageTokenHash = "seeded",
            });
        });
    }

    private static Link Bookable(string slug) => new()
    {
        Slug = slug,
        Name = slug,
        PatternId = PatternId,
        Durations = [new LinkDuration { Minutes = DurationMinutes, SortOrder = 0 }],
    };

    /// <summary>Only the machine-readable half of ProblemDetails; nothing else is asserted on.</summary>
    private sealed record ProblemWithCode(string Code);

    private static SaveSettingsRequest Body(string timeZoneId) => new()
    {
        TimeZoneId = timeZoneId,
        MarginMinutes = NewMargin,
        MinNoticeMinutes = NewMinNotice,
        DailyCap = NewDailyCap,
        StartIncrementMinutes = NewStartIncrement,
        ProposalHoldHours = NewProposalHold,
    };

    /// <summary>
    /// One link per <paramref name="limits"/> entry, each with one booking on it. The bookings are
    /// seeded rather than made through the booking endpoint: what is asserted about them is that
    /// nothing moves their instant, and a booking that arrived through availability would be
    /// making that claim about the seeding path as well.
    /// </summary>
    private static void Seed(KairosApp app, params Action<Link>[] limits)
    {
        app.Seed(context =>
        {
            context.Patterns.Add(new Pattern { Id = PatternId, Name = "Weekdays" });

            for (var index = 0; index < limits.Length; index++)
            {
                var link = new Link
                {
                    Slug = $"link-{index}",
                    Name = $"Link {index}",
                    PatternId = PatternId,
                };

                limits[index](link);
                context.Links.Add(link);

                // Staggered, because the no-double-booking rule is a unique index on the start:
                // two seeded meetings at one instant is the collision that index exists for.
                var start = SeededStartUtc + (index * HalfAnHour);

                context.Bookings.Add(new Booking
                {
                    Link = link,
                    StartUtc = start,
                    EndUtc = start + HalfAnHour,
                    Status = BookingStatus.Confirmed,
                    ManageTokenHash = $"hash-{index}",
                });
            }
        });
    }
}
