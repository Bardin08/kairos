using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Kairos.Api.Features.Admin;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Shared;

using Microsoft.EntityFrameworkCore;

using NodaTime;
using NodaTime.Text;

using Shouldly;

// Aliased because ASP.NET Core's authentication namespace brings its own SystemClock, and the one
// this suite means is the one every instant in the application is read from.
using SystemClock = NodaTime.SystemClock;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The availability write side over the real pipeline. Only the rules that are wrong in ways
/// nothing else would notice are pinned here: a rename works or it visibly does not, but an
/// overlapping window and a cascaded delete both look like success and are found weeks later —
/// one as a duplicated time on the booking page, the other as a link that quietly stopped
/// offering anything.
/// </summary>
public class PatternsAdminTests
{
    private const string Slug = "consulting";
    private const string LinkName = "Consulting call";
    private const int DurationMinutes = 25;

    private const int MinutesPerHour = 60;

    private const int Monday = (int)IsoDayOfWeek.Monday;
    private const int Tuesday = (int)IsoDayOfWeek.Tuesday;
    private const int Wednesday = (int)IsoDayOfWeek.Wednesday;

    /// <summary>Minutes from local midnight, which is what the column stores.</summary>
    private const int NineAm = 9 * MinutesPerHour;
    private const int TenAm = 10 * MinutesPerHour;
    private const int Noon = 12 * MinutesPerHour;
    private const int TwoPm = 14 * MinutesPerHour;
    private const int FourPm = 16 * MinutesPerHour;
    private const int FivePm = 17 * MinutesPerHour;

    /// <summary>One past what a day may hold, which is the boundary both refusals turn on.</summary>
    private const int WindowsPastTheDayCap = PatternsAdminEndpoints.MaxWindowsPerDate + 1;

    private const int RulesPastTheWeekCap = PatternsAdminEndpoints.MaxWindowsPerWeek + 1;

    /// <summary>Days ahead of the seeding, so real-clock minimum notice never decides a test.</summary>
    private const int WellAhead = 30;

    /// <summary>
    /// The 30th of February: date-shaped and not a date, which is why the parse decides and a
    /// route constraint could not. Both override routes take it, so both have to refuse it alike.
    /// </summary>
    private const string DateThatIsNotADate = "2026-02-30";

    /// <summary>
    /// A year the ISO pattern reads and the stored type cannot hold, which fails a step later than
    /// the 30th of February and so is the case that catches a guard looking only at the parse.
    /// </summary>
    private const string DateBeforeTheFirstYearStorable = "0000-01-01";

    private static readonly Guid PatternId = Guid.CreateVersion7();
    private static readonly Guid LinkId = Guid.CreateVersion7();

    [Fact]
    public async Task ReplaceRules_WithAWholeNewSet_StoresExactlyThatSet()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();
        var pattern = await CreatePatternWithMondayNineToFiveAsync(client);

        var response = await ReplaceRulesAsync(
            client,
            pattern,
            [
                new { isoDayOfWeek = Tuesday, startMinute = TenAm, endMinute = FourPm },
                new { isoDayOfWeek = Wednesday, startMinute = TenAm, endMinute = FourPm },
            ]);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stored = await PatternOfAsync(response);
        stored.Rules.Count.ShouldBe(2);
        stored.Rules.ShouldAllBe(rule => rule.StartMinute == TenAm);
        stored.Rules.Select(rule => rule.IsoDayOfWeek).ShouldBe([Tuesday, Wednesday]);
    }

    /// <summary>
    /// One bad window in the set is the whole set refused. A day at a time, a half-applied save
    /// leaves the operator looking at a week they did not ask for and cannot tell apart from one
    /// they did — so the validation runs before anything is written.
    /// </summary>
    [Fact]
    public async Task ReplaceRules_WhenOneWindowIsInverted_StoresNoneOfThem()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();
        var pattern = await CreatePatternWithMondayNineToFiveAsync(client);

        var response = await ReplaceRulesAsync(
            client,
            pattern,
            [
                new { isoDayOfWeek = Tuesday, startMinute = TenAm, endMinute = FourPm },
                new { isoDayOfWeek = Wednesday, startMinute = FourPm, endMinute = TenAm },
            ]);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var after = await client.GetFromJsonAsync<IReadOnlyList<PatternResponse>>(
            $"{ApiVersions.V1}/admin/patterns", TestContext.Current.CancellationToken);
        var unchanged = after!.Single(candidate => candidate.Id == pattern.Id);
        unchanged.Rules.Count.ShouldBe(1);
        unchanged.Rules[0].IsoDayOfWeek.ShouldBe(Monday);
    }

    [Fact]
    public async Task ReplaceRules_WithAnEmptySet_ClosesEveryDay()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();
        var pattern = await CreatePatternWithMondayNineToFiveAsync(client);

        var response = await ReplaceRulesAsync(client, pattern, []);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PatternOfAsync(response)).Rules.ShouldBeEmpty();
    }

    /// <summary>
    /// The window this whole endpoint exists to refuse. Two rules covering 09:00-17:00 and
    /// 10:00-16:00 on one Monday do not produce a longer day — they produce every 10:00-onwards
    /// start twice, which reaches the invitee as a list with duplicates in it and reads as a
    /// rendering fault rather than as the data fault it is.
    /// </summary>
    [Fact]
    public async Task ReplaceRules_WithTwoWindowsOverlappingOnOneDay_IsRefused()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();
        var pattern = await CreatePatternWithMondayNineToFiveAsync(client);

        var response = await ReplaceRulesAsync(
            client,
            pattern,
            [
                new { isoDayOfWeek = Monday, startMinute = NineAm, endMinute = FivePm },
                new { isoDayOfWeek = Monday, startMinute = TenAm, endMinute = FourPm },
            ]);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(response)).ShouldBe(PatternsAdminEndpoints.RuleOverlapsCode);
    }

    /// <summary>
    /// Without this, the overlap test passes on an implementation that refuses any second window
    /// on a day — which would silently kill split days.
    /// </summary>
    [Fact]
    public async Task ReplaceRules_WithTwoWindowsTouchingOnOneDay_IsAccepted()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();
        var pattern = await CreatePatternWithMondayNineToFiveAsync(client);

        var response = await ReplaceRulesAsync(
            client,
            pattern,
            [
                new { isoDayOfWeek = Monday, startMinute = NineAm, endMinute = Noon },
                new { isoDayOfWeek = Monday, startMinute = Noon, endMinute = FivePm },
            ]);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PatternOfAsync(response)).Rules.Count.ShouldBe(2);
    }

    /// <summary>
    /// The case the collection cap alone lets through: a weekly body well inside its overall
    /// bound of six windows times seven days, with all of one day's share and one more on Monday.
    /// The per-day rule is the real rule; the collection cap only stops the allocation.
    /// </summary>
    [Fact]
    public async Task ReplaceRules_WithMoreWindowsOnOneDayThanItMayHold_IsRefusedWithACode()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();
        var pattern = await CreatePatternWithMondayNineToFiveAsync(client);

        var response = await ReplaceRulesAsync(client, pattern, MondayRulesPastTheDayCap());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOfAsync(response)).ShouldBe(PatternsAdminEndpoints.TooManyWindowsCode);
    }

    /// <summary>
    /// Finding 2 on the weekly half: Rules carried no length bound either, so a body up to
    /// Kestrel's 30 MB cap was deserialized, grouped and sorted a day at a time before anything
    /// refused it. Every body past the weekly bound also puts a seventh window on some day, so
    /// what tells the collection cap apart from the per-day one is which of them answers: this
    /// refusal names the field and carries no code, which only the attribute does.
    /// </summary>
    [Fact]
    public async Task ReplaceRules_WithMoreWindowsInTheWeekThanItMayHold_IsRefusedByTheAttribute()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();
        var pattern = await CreatePatternWithMondayNineToFiveAsync(client);

        var response = await ReplaceRulesAsync(client, pattern, WeeklyRulesPastTheWeekCap());
        var problem = await ReadJsonAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        problem.TryGetProperty("errors", out var errors).ShouldBeTrue();
        errors.TryGetProperty(nameof(ReplaceRulesRequest.Rules), out _).ShouldBeTrue();
        problem.TryGetProperty("code", out _).ShouldBeFalse();
    }

    /// <summary>
    /// The refusal that has to name names. A cascade here would delete the pattern and leave the
    /// link resolvable, active and offering nothing — an outage the operator did not ask for and
    /// cannot see the cause of. Which links, not how many: the operator has to go and repoint
    /// them, and a count sends them hunting through the links table for the answer this request
    /// already has.
    /// </summary>
    [Fact]
    public async Task DeletePattern_WhileALinkStandsOnIt_IsRefusedAndNamesTheLink()
    {
        using var app = NewApp();
        using var client = app.CreateOperatorClient();

        var response = await client.DeleteAsync(PatternUrl, TestContext.Current.CancellationToken);

        // Read once. The body is a stream, and a second ReadFromJsonAsync on the same response
        // finds it closed rather than rewound.
        var problem = await ReadJsonAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        problem.GetProperty("code").GetString().ShouldBe(PatternsAdminEndpoints.PatternInUseCode);
        problem.GetProperty("title").GetString().ShouldNotBeNull().ShouldContain(LinkName);
        app.Read(context => context.Patterns.Count()).ShouldBe(1);
    }

    /// <summary>
    /// The blocked day, proved where it matters rather than in the row that stores it. A form
    /// that only offered "change the times" could not express a holiday — so what is pinned is
    /// the public booking page, through the same HTTP the invitee's browser uses, and then that
    /// the date is still an override: one that deleted its rows instead would put the date back
    /// on the weekly rules, where nothing can tell it from a date nobody ever touched.
    /// </summary>
    [Fact]
    public async Task SaveOverride_WithNoWindows_BlocksTheDate()
    {
        using var app = NewApp();
        using var client = app.CreateOperatorClient();
        var day = Day();

        var before = await SlotsAsync(app, Formatted(day));

        var response = await SaveOverrideAsync(client, day, []);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        before.ShouldNotBeEmpty();
        (await SlotsAsync(app, Formatted(day))).ShouldBeEmpty();
        (await PatternOfAsync(response)).Overrides
            .Single(replacement => replacement.Date == day.ToDateOnly())
            .Windows.ShouldBeEmpty();
    }

    /// <summary>
    /// A date splits the way a weekday already can, pinned where it is visible: not two rows, but
    /// a booking page with a morning, an afternoon and nothing offered in between.
    /// </summary>
    [Fact]
    public async Task SaveOverride_WithTwoWindows_StoresBoth()
    {
        using var app = NewApp();
        using var client = app.CreateOperatorClient();
        var day = Day();

        var response = await SaveOverrideAsync(
            client,
            day,
            [
                new { startMinute = NineAm, endMinute = Noon },
                new { startMinute = TwoPm, endMinute = FivePm },
            ]);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PatternOfAsync(response)).Overrides
            .Single(replacement => replacement.Date == day.ToDateOnly())
            .Windows.ShouldBe(
                [new OverrideWindowResponse(NineAm, Noon), new OverrideWindowResponse(TwoPm, FivePm)]);

        var offered = (await SlotsAsync(app, Formatted(day))).Select(MinuteOfDayOf).ToArray();
        offered.ShouldContain(NineAm);
        offered.ShouldContain(TwoPm);
        offered.ShouldAllBe(minute => minute < Noon || minute >= TwoPm);
    }

    /// <summary>
    /// Half a date is worse than none of it. Two windows covering 09:00-17:00 and 10:00-16:00
    /// offer every 10:00-onwards start twice, and a save that cleared the date before finding out
    /// would leave the operator with a date they never asked for — so the date that was there is
    /// what is still there afterwards.
    /// </summary>
    [Fact]
    public async Task SaveOverride_WithTwoWindowsOverlappingOnTheDate_IsRefused()
    {
        using var app = NewApp();
        using var client = app.CreateOperatorClient();
        var day = Day();
        await SaveOverrideAsync(client, day, [new { startMinute = NineAm, endMinute = Noon }]);

        var response = await SaveOverrideAsync(
            client,
            day,
            [
                new { startMinute = NineAm, endMinute = FivePm },
                new { startMinute = TenAm, endMinute = FourPm },
            ]);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(response)).ShouldBe(PatternsAdminEndpoints.RuleOverlapsCode);
        (await SeededPatternAsync(client)).Overrides
            .Single(replacement => replacement.Date == day.ToDateOnly())
            .Windows.ShouldBe([new OverrideWindowResponse(NineAm, Noon)]);
    }

    /// <summary>
    /// The review claimed nothing stopped a date holding the same window twice. One request
    /// cannot: two identical windows are the strongest overlap there is — the second starts
    /// exactly where the first does — so the rule that refuses 09:00-17:00 beside 10:00-16:00
    /// refuses this too. Pinned because the claim will be read again by whoever revisits the
    /// finding, and the answer should be a test rather than an argument.
    /// </summary>
    [Fact]
    public async Task SaveOverride_WithTheSameWindowTwice_IsRefusedAsAnOverlap()
    {
        using var app = NewApp();
        using var client = app.CreateOperatorClient();
        var day = Day();

        var response = await SaveOverrideAsync(
            client,
            day,
            [
                new { startMinute = NineAm, endMinute = Noon },
                new { startMinute = NineAm, endMinute = Noon },
            ]);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(response)).ShouldBe(PatternsAdminEndpoints.RuleOverlapsCode);
        (await SeededPatternAsync(client)).Overrides.ShouldBeEmpty();
    }

    /// <summary>
    /// Finding 2 of the review: Windows carried [Required] and no length bound, so a body up to
    /// Kestrel's 30 MB cap was deserialized, projected into a tuple sequence and sorted before
    /// anything refused it. Seven is one past what a day can carry, which is the boundary worth
    /// pinning — six is already a morning, an afternoon and four more the operator meant. The
    /// code is what is asserted, because a date's seventh window is the same operator mistake as
    /// a Monday's and a client branches on <c>code</c>: a bound on the body tight enough to
    /// refuse this first would answer the two routes differently.
    /// </summary>
    [Fact]
    public async Task SaveOverride_WithMoreWindowsThanADayMayHold_IsRefusedWithACode()
    {
        using var app = NewApp();
        using var client = app.CreateOperatorClient();

        var response = await SaveOverrideAsync(client, Day(), OverrideWindows(WindowsPastTheDayCap));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOfAsync(response)).ShouldBe(PatternsAdminEndpoints.TooManyWindowsCode);
    }

    /// <summary>
    /// The allocation guard on this route, which the coded refusal above cannot stand in for: it
    /// runs inside the handler, on a list already bound. Refused before that, so the field is
    /// named and no code comes back — and without the bound the count above would answer instead,
    /// which is what these assertions rule out.
    /// </summary>
    [Fact]
    public async Task SaveOverride_WithMoreWindowsThanAnyBodyMayCarry_IsRefusedByTheAttribute()
    {
        using var app = NewApp();
        using var client = app.CreateOperatorClient();

        var response = await SaveOverrideAsync(client, Day(), OverrideWindowsPastTheWeekCap());
        var problem = await ReadJsonAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        problem.TryGetProperty("errors", out var errors).ShouldBeTrue();
        errors.TryGetProperty(nameof(SaveOverrideRequest.Windows), out _).ShouldBeTrue();
        problem.TryGetProperty("code", out _).ShouldBeFalse();
    }

    /// <summary>
    /// The other side of the boundary, without which a cap written one off would refuse a day the
    /// operator is allowed to have and nothing here would notice.
    /// </summary>
    [Fact]
    public async Task SaveOverride_WithExactlyAsManyWindowsAsADayMayHold_IsStored()
    {
        using var app = NewApp();
        using var client = app.CreateOperatorClient();
        var day = Day();

        var response = await SaveOverrideAsync(
            client, day, OverrideWindows(PatternsAdminEndpoints.MaxWindowsPerDate));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PatternOfAsync(response)).Overrides
            .Single(replacement => replacement.Date == day.ToDateOnly())
            .Windows.Count.ShouldBe(PatternsAdminEndpoints.MaxWindowsPerDate);
    }

    /// <summary>
    /// Left to the minimal-API binder this was a <c>BadHttpRequestException</c> from inside
    /// RequestDelegateFactory: a 500 under the test host and a bodiless 400 in production, on a
    /// surface where every other malformed input carries a code the workbench can act on.
    /// </summary>
    [Theory]
    [InlineData(DateThatIsNotADate)]
    [InlineData(DateBeforeTheFirstYearStorable)]
    public async Task SaveOverride_OnADateSegmentItCannotStore_IsABadRequestNamingTheCode(
        string date)
    {
        using var app = NewApp();
        using var client = app.CreateOperatorClient();

        var response = await client.PutAsJsonAsync(
            $"{PatternUrl}/overrides/{date}",
            new { windows = Array.Empty<object>() },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOfAsync(response)).ShouldBe(PatternsAdminEndpoints.DateInvalidCode);
    }

    /// <summary>The other half of the same pair of routes, which binds the same segment.</summary>
    [Theory]
    [InlineData(DateThatIsNotADate)]
    [InlineData(DateBeforeTheFirstYearStorable)]
    public async Task RemoveOverride_OnADateSegmentItCannotStore_IsABadRequestNamingTheCode(
        string date)
    {
        using var app = NewApp();
        using var client = app.CreateOperatorClient();

        var response = await client.DeleteAsync(
            $"{PatternUrl}/overrides/{date}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOfAsync(response)).ShouldBe(PatternsAdminEndpoints.DateInvalidCode);
    }

    /// <summary>
    /// The seam the whole tab exists to move: a pattern built here, through the admin API, and
    /// then read by an anonymous browser as times it can book. Nothing in between is stubbed —
    /// the link is repointed the way it would be from the links editor, and the last read is the
    /// public endpoint with no session on it.
    /// </summary>
    [Fact]
    public async Task CreatePatternAndRule_OnceALinkStandsOnIt_ChangesWhatTheBookingPageOffers()
    {
        using var app = NewApp();
        using var client = app.CreateOperatorClient();
        // A Tuesday, so the seeded Monday-only pattern offers nothing on it and the new pattern
        // is the only thing that could put a time there.
        var day = Day().PlusDays(1);
        var date = Formatted(day);

        var before = await SlotsAsync(app, date);

        var created = await client.PostAsJsonAsync(
            $"{ApiVersions.V1}/admin/patterns",
            new { name = "Tuesdays" },
            TestContext.Current.CancellationToken);
        var pattern = await PatternOfAsync(created);

        var ruled = await ReplaceRulesAsync(
            client,
            pattern,
            [new { isoDayOfWeek = Tuesday, startMinute = NineAm, endMinute = FivePm }]);

        // Attached through the database rather than through an editor this screen does not have:
        // which pattern a link stands on is the link's own field, and editing it here would be
        // the link editing this tab is not allowed to grow.
        app.Seed(context => context.Links
            .Where(link => link.Id == LinkId)
            .ExecuteUpdate(link => link.SetProperty(
                candidate => candidate.PatternId, pattern.Id)));

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        ruled.StatusCode.ShouldBe(HttpStatusCode.OK);
        before.ShouldBeEmpty();
        (await SlotsAsync(app, date)).ShouldNotBeEmpty();
    }

    /// <summary>
    /// Two admin tabs saving one date at once, over the real pipeline. What is asserted is what
    /// holds however the two interleave: neither is answered with a stack trace, and the date is
    /// left holding one writer's set rather than the union of both. Whether the loser is refused
    /// or merely replaces the winner is the test host's to decide and is not asserted — under
    /// load the two requests serialize and both answer 200, which is correct and would make a
    /// stricter assertion a timing measurement wearing a behaviour test's clothes. The refusal
    /// itself is pinned where it can be forced: the version check in <c>SchemaTests</c>, and the
    /// mapping from it in <c>ConcurrentWriteTests</c>.
    /// </summary>
    [Fact]
    public async Task SaveOverride_FromTwoWritersAtOnce_LeavesOneWritersSetAndNeverAServerError()
    {
        using var app = NewApp();
        using var client = app.CreateOperatorClient();
        var day = Day();

        var morning = SaveOverrideAsync(client, day, [new { startMinute = NineAm, endMinute = Noon }]);
        var afternoon = SaveOverrideAsync(client, day, [new { startMinute = TwoPm, endMinute = FivePm }]);

        var answers = await Task.WhenAll(morning, afternoon);

        answers.ShouldAllBe(answer => answer.StatusCode != HttpStatusCode.InternalServerError);
        foreach (var refused in answers.Where(answer => answer.StatusCode != HttpStatusCode.OK))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await CodeOfAsync(refused)).ShouldBe(PatternsAdminEndpoints.OverrideDateTakenCode);
        }

        // One window rather than two, which is what rules out the union — and it is one of the
        // two that were sent rather than anything in between.
        var stored = (await SeededPatternAsync(client)).Overrides
            .Single(replacement => replacement.Date == day.ToDateOnly())
            .Windows;

        stored.ShouldHaveSingleItem().ShouldBeOneOf(
            new OverrideWindowResponse(NineAm, Noon),
            new OverrideWindowResponse(TwoPm, FivePm));

        foreach (var answer in answers)
        {
            answer.Dispose();
        }
    }

    private static string PatternUrl => $"{ApiVersions.V1}/admin/patterns/{PatternId}";

    /// <summary>
    /// A pattern of one Monday window, seeded through the endpoint under test rather than through
    /// the database, so what these tests replace is a set the same endpoint stored.
    /// </summary>
    private static async Task<PatternResponse> CreatePatternWithMondayNineToFiveAsync(HttpClient client)
    {
        var created = await client.PostAsJsonAsync(
            $"{ApiVersions.V1}/admin/patterns",
            new { name = "Weekdays" },
            TestContext.Current.CancellationToken);

        var ruled = await ReplaceRulesAsync(
            client,
            await PatternOfAsync(created),
            [new { isoDayOfWeek = Monday, startMinute = NineAm, endMinute = FivePm }]);

        return await PatternOfAsync(ruled);
    }

    private static Task<HttpResponseMessage> ReplaceRulesAsync(
        HttpClient client,
        PatternResponse pattern,
        object[] rules) =>
        client.PutAsJsonAsync(
            $"{ApiVersions.V1}/admin/patterns/{pattern.Id}/rules",
            new { rules },
            TestContext.Current.CancellationToken);

    /// <summary>Everything the seeded date offers, which is the whole request body.</summary>
    private static Task<HttpResponseMessage> SaveOverrideAsync(
        HttpClient client,
        LocalDate day,
        object[] windows) =>
        client.PutAsJsonAsync(
            $"{PatternUrl}/overrides/{Formatted(day)}",
            new { windows },
            TestContext.Current.CancellationToken);

    /// <summary>
    /// Consecutive hour-long windows from 09:00, none inverted and none overlapping, so the only
    /// rule a set of them can break is the count and no cap test passes for another reason.
    /// </summary>
    private static object[] OverrideWindows(int count) =>
        [.. Enumerable.Range(0, count).Select(hour => new
        {
            startMinute = NineAm + (hour * MinutesPerHour),
            endMinute = NineAm + ((hour + 1) * MinutesPerHour),
        })];

    /// <summary>
    /// A week's worth of windows and one more, all on one date. One minute each and end to end,
    /// which is legal — windows that merely touch are a split day — so the only rule the set
    /// breaks is the bound, and 43 of them still land well inside the day.
    /// </summary>
    private static object[] OverrideWindowsPastTheWeekCap() =>
        [.. Enumerable.Range(0, RulesPastTheWeekCap).Select(minute => new
        {
            startMinute = minute,
            endMinute = minute + 1,
        })];

    private static object[] MondayRulesPastTheDayCap() =>
        [.. Enumerable.Range(0, WindowsPastTheDayCap).Select(hour => new
        {
            isoDayOfWeek = Monday,
            startMinute = NineAm + (hour * MinutesPerHour),
            endMinute = NineAm + ((hour + 1) * MinutesPerHour),
        })];

    /// <summary>
    /// One past the weekly bound, dealt round the week so every day gets six of them and Monday
    /// the odd one — the fullest legal week plus a window, which is the only way past that bound.
    /// </summary>
    private static object[] WeeklyRulesPastTheWeekCap() =>
        [.. Enumerable.Range(0, RulesPastTheWeekCap).Select(rule => new
        {
            isoDayOfWeek = Monday + (rule % PatternsAdminEndpoints.DaysInWeek),
            startMinute = NineAm + ((rule / PatternsAdminEndpoints.DaysInWeek) * MinutesPerHour),
            endMinute = NineAm + (((rule / PatternsAdminEndpoints.DaysInWeek) + 1) * MinutesPerHour),
        })];

    /// <summary>The seeded pattern read back through the list, for what a refused write left.</summary>
    private static async Task<PatternResponse> SeededPatternAsync(HttpClient client)
    {
        var patterns = await client.GetFromJsonAsync<IReadOnlyList<PatternResponse>>(
            $"{ApiVersions.V1}/admin/patterns", TestContext.Current.CancellationToken);

        return patterns!.Single(candidate => candidate.Id == PatternId);
    }

    /// <summary>The public read, unauthenticated, exactly as an invitee's browser makes it.</summary>
    private static async Task<string[]> SlotsAsync(KairosApp app, string day)
    {
        var slots = await app.Client.GetFromJsonAsync<string[]>(
            $"{ApiVersions.V1}/links/{Slug}/slots?from={day}&to={day}&duration={DurationMinutes}",
            TestContext.Current.CancellationToken);

        return slots ?? [];
    }

    /// <summary>
    /// A slot's wall-clock minute, which the stored windows are also in. The seeded settings put
    /// the organizer on UTC, so the instant the endpoint answers with needs no zone of its own.
    /// </summary>
    private static int MinuteOfDayOf(string slot)
    {
        var start = InstantPattern.ExtendedIso.Parse(slot).Value.InUtc().TimeOfDay;
        return (start.Hour * MinutesPerHour) + start.Minute;
    }

    private static KairosApp NewApp()
    {
        var app = new KairosApp();
        app.Seed(Seed);
        return app;
    }

    /// <summary>
    /// One pattern with one Monday window, and one link standing on it. The link is what makes
    /// the delete refusal reachable and what makes the blocked date observable from outside.
    /// </summary>
    private static void Seed(KairosDbContext context)
    {
        context.Patterns.Add(new Pattern { Id = PatternId, Name = "Weekdays" });
        context.PatternRules.Add(new PatternRule
        {
            Id = Guid.CreateVersion7(),
            PatternId = PatternId,
            IsoDayOfWeek = Monday,
            StartMinute = NineAm,
            EndMinute = FivePm,
        });
        context.Links.Add(new Link
        {
            Id = LinkId,
            Slug = Slug,
            Name = LinkName,
            PatternId = PatternId,
            Active = true,
        });
        context.LinkDurations.Add(new LinkDuration
        {
            LinkId = LinkId,
            Minutes = DurationMinutes,
            SortOrder = 0,
        });
    }

    /// <summary>
    /// Computed rather than written down. The application reads the real clock, so a literal date
    /// would turn these tests into a suite that passes until it silently stops.
    /// </summary>
    private static LocalDate Day() =>
        SystemClock.Instance.GetCurrentInstant().InUtc().Date.PlusDays(WellAhead).Next(IsoDayOfWeek.Monday);

    private static string Formatted(LocalDate day) => LocalDatePattern.Iso.Format(day);

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).GetProperty("code").GetString();

    private static async Task<PatternResponse> PatternOfAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<PatternResponse>(
            TestContext.Current.CancellationToken))!;

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}
