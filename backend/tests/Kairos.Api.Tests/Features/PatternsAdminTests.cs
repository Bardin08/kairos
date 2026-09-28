using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Kairos.Api.Features.Admin;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Shared;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using NodaTime;
using NodaTime.Text;

using Shouldly;

// Aliased because ASP.NET Core's authentication namespace brings its own SystemClock, and the one
// this suite means is the one every instant in the application is read from.
using SystemClock = NodaTime.SystemClock;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The availability write side over the real pipeline. Only the rules that fail invisibly are
/// pinned: an overlapping window and a cascaded delete both look like success and are found weeks
/// later, one as a duplicated time on the booking page, the other as a link offering nothing.
/// </summary>
public class PatternsAdminTests
{
    private const string Slug = "consulting";
    private const string LinkName = "Consulting call";
    private const int DurationMinutes = 25;

    private const string FirstPatternName = "Weekdays";

    /// <summary>A second pattern, so a rename has somewhere to collide from.</summary>
    private const string SecondPatternName = "Evenings";

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

    /// <summary>Two tabs on one date, which is the smallest number that is a race at all.</summary>
    private const int RacingWriters = 2;

    /// <summary>A save with no windows, which is how the operator blocks a date.</summary>
    private const int NoWindows = 0;

    private const int OneWindow = 1;

    /// <summary>How EF's SQLite provider writes a DateOnly, and so the only spelling a DELETE by date matches.</summary>
    private const string StoredDateFormat = "yyyy-MM-dd";

    /// <summary>
    /// How many times the rules race below is run. Whether two requests interleave is the
    /// scheduler's decision, and a round that does not proves nothing — measured with the guard's
    /// predicate removed, a single round let the union through on 3 of 9 whole-suite runs, five
    /// rounds caught it on 6 of 6. Alone the test is far luckier; the suite is where it runs.
    /// </summary>
    private const int RacedRounds = 5;

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
    /// 10:00-16:00 on one Monday do not make a longer day — they offer every 10:00-onwards start
    /// twice, which reaches the invitee as duplicates and reads as a rendering fault.
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
    /// which cap answered is the tell: this refusal names the field and carries no code.
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
    /// The refusal that has to name names. A cascade would delete the pattern and leave the link
    /// resolvable, active and offering nothing. Which links, not how many: the operator has to
    /// repoint them, and a count sends them hunting for an answer this request already has.
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
    /// The blocked day, proved on the public booking page rather than in the row that stores it —
    /// a form offering only "change the times" could not express a holiday. And the date is still
    /// an override: one that deleted its rows would put it back on the weekly rules.
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
    /// Removing a date is not blocking it: the weekly rules come back. The date's windows go by the
    /// foreign key's cascade, since the route deletes with ExecuteDelete and loads nothing.
    /// </summary>
    [Fact]
    public async Task RemoveOverride_OnASplitDate_PutsTheDateBackOnTheWeeklyRules()
    {
        using var app = NewApp();
        using var client = app.CreateOperatorClient();
        var day = Day();
        var weekly = await SlotsAsync(app, Formatted(day));
        await SaveOverrideAsync(
            client,
            day,
            [
                new { startMinute = NineAm, endMinute = Noon },
                new { startMinute = TwoPm, endMinute = FivePm },
            ]);

        var response = await client.DeleteAsync(
            $"{PatternUrl}/overrides/{Formatted(day)}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PatternOfAsync(response)).Overrides.ShouldBeEmpty();
        (await SlotsAsync(app, Formatted(day))).ShouldBe(weekly);
    }

    /// <summary>
    /// Half a date is worse than none of it. Two windows covering 09:00-17:00 and 10:00-16:00
    /// offer every 10:00-onwards start twice, and a save that cleared the date before finding out
    /// would leave a date the operator never asked for. What was there is still there.
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
    /// Finding 2 of the review: Windows carried [Required] and no length bound, so a body up to
    /// Kestrel's 30 MB cap was deserialized, projected and sorted before anything refused it.
    /// Seven is one past what a day can carry — six is already a morning, an afternoon and four
    /// more the operator meant. The code is asserted because a date's seventh window is the same
    /// mistake as a Monday's and clients branch on <c>code</c>: a tighter body bound would answer
    /// the two routes differently.
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
    /// The allocation guard the coded refusal above cannot stand in for: that one runs inside the
    /// handler, on a list already bound. Refused before that, so the field is named and no code
    /// comes back — without the bound the count would answer instead.
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
    /// The seam the whole tab exists to move: a pattern built through the admin API, then read by
    /// an anonymous browser as times it can book. Nothing between is stubbed — the link is
    /// repointed as the links editor would, and the last read carries no session.
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
    /// Two admin tabs saving one date at once, over the real pipeline. What holds however they
    /// interleave: neither is answered with a stack trace, and the date is left holding one
    /// writer's set rather than the union of both.
    /// </summary>
    /// <remarks>
    /// Whether the loser is refused or merely replaces the winner is not asserted, and the
    /// measurements are why: with the barrier the strict form — one 200 and one 409 — passed
    /// 20 of 20 runs alone and 10 of 10 under the full suite, without it 1 in 5 failed with both
    /// answering 200. The barrier aligns the sends, not where the handlers read, so ten green
    /// runs are ten samples. The refusal is forced instead in <c>SchemaTests</c>' version check
    /// and <c>ConcurrentWriteTests</c>' mapping.
    /// </remarks>
    [Fact]
    public async Task SaveOverride_FromTwoWritersAtOnce_LeavesOneWritersSetAndNeverAServerError()
    {
        using var app = NewApp();
        using var client = app.CreateOperatorClient();
        var day = Day();

        using var barrier = new Barrier(RacingWriters);
        var morning = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await SaveOverrideAsync(client, day, [new { startMinute = NineAm, endMinute = Noon }]);
        });
        var afternoon = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await SaveOverrideAsync(client, day, [new { startMinute = TwoPm, endMinute = FivePm }]);
        });

        var answers = await Task.WhenAll(morning, afternoon);

        answers.ShouldAllBe(answer => answer.StatusCode != HttpStatusCode.InternalServerError);
        foreach (var refused in answers.Where(answer => answer.StatusCode != HttpStatusCode.OK))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await CodeOfAsync(refused)).ShouldBe(PatternsAdminEndpoints.PatternChangedCode);
        }

        // One window rather than two, which is what rules out the union — and it is one of the
        // two that were sent rather than anything in between.
        var stored = (await SeededPatternAsync(client)).Overrides
            .Single(replacement => replacement.Date == day.ToDateOnly())
            .Windows;

        stored.ShouldHaveSingleItem().ShouldBeOneOf(
            new OverrideWindowResponse(NineAm, Noon),
            new OverrideWindowResponse(TwoPm, FivePm));
    }

    /// <summary>
    /// The defect this whole change exists for. Two operators saving a week on a pattern with no
    /// rules yet: nothing is deleted, so nothing raises the way it does on a pattern that has
    /// rules, and both sets used to land — measured with the guard removed, both writers answered
    /// 200 and the stored week held Monday and Wednesday, days neither operator asked for.
    /// </summary>
    [Fact]
    public async Task ReplaceRules_FromTwoWritersAtOnceOnAPatternWithNoRules_KeepsOneWritersWeek()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();
        var created = await PatternOfAsync(await client.PostAsJsonAsync(
            $"{ApiVersions.V1}/admin/patterns",
            new { name = FirstPatternName },
            TestContext.Current.CancellationToken));

        for (var round = 0; round < RacedRounds; round++)
        {
            // The hole is specific to a week holding nothing, and the round before this one left
            // it holding a day, so every round starts the pattern empty again.
            (await ReplaceRulesAsync(client, created, [])).StatusCode.ShouldBe(HttpStatusCode.OK);

            using var barrier = new Barrier(RacingWriters);
            var monday = ReplaceRulesWhenReleasedAsync(client, created, Monday, barrier);
            var wednesday = ReplaceRulesWhenReleasedAsync(client, created, Wednesday, barrier);

            var answers = await Task.WhenAll(monday, wednesday);

            foreach (var refused in answers.Where(answer => answer.StatusCode != HttpStatusCode.OK))
            {
                refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
                (await CodeOfAsync(refused)).ShouldBe(PatternsAdminEndpoints.PatternChangedCode);
            }

            // One writer's week, never both. The union is the defect this closes.
            var stored = (await StoredPatternAsync(client, created.Id)).Rules;
            stored.ShouldHaveSingleItem().IsoDayOfWeek.ShouldBeOneOf(Monday, Wednesday);
        }
    }

    /// <summary>
    /// A save on a blocked date that another tab removed after this one read it. The date's row is
    /// all the save stands on, and a removal does not raise the version, so the save itself has to
    /// notice: otherwise windows land on a row that is gone, a foreign-key 500, or an empty save
    /// answers 200 over a date that is no longer blocked.
    /// </summary>
    /// <remarks>
    /// The removal is made by an interceptor between the save's read and its write, so the
    /// interleave happens on every run rather than when the scheduler allows.
    /// </remarks>
    [Theory]
    [InlineData(NoWindows)]
    [InlineData(OneWindow)]
    public async Task SaveOverride_OnABlockedDateRemovedUnderIt_IsRefusedAsChanged(int windows)
    {
        using var app = NewApp();
        using var client = app.CreateOperatorClient();
        var day = Day();
        (await SaveOverrideAsync(client, day, OverrideWindows(NoWindows))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var removal = new RemoveDateBeforeSave(app.ConnectionString, PatternId, day.ToDateOnly());
        using var racedClient = app.CreateOperatorClient(services =>
        {
            services.RemoveAll<DbContextOptions<KairosDbContext>>();
            services.AddSingleton(new DbContextOptionsBuilder<KairosDbContext>()
                .UseSqlite(app.ConnectionString)
                .AddInterceptors(removal)
                .Options);
        });

        var response = await SaveOverrideAsync(racedClient, day, OverrideWindows(windows));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(response)).ShouldBe(PatternsAdminEndpoints.PatternChangedCode);
        (await SeededPatternAsync(client)).Overrides.ShouldBeEmpty();
    }

    /// <summary>
    /// The reason there are two columns rather than one. On a single token a weekly-rules save
    /// and a date's save raced, so an operator blocking a holiday could be refused because
    /// somebody else was editing Tuesday's hours. Both answers are 200 here.
    /// </summary>
    /// <remarks>
    /// One round, unlike the race above, and not an oversight: the loser waits on the write lock
    /// rather than racing for it, so the interleave happens every time. Measured — with
    /// RulesVersion made a concurrency token, this fails on all ten runs of a single round.
    /// </remarks>
    [Fact]
    public async Task ReplaceRulesAndSaveOverride_AtOnce_DoNotRefuseEachOther()
    {
        using var app = NewApp();
        using var client = app.CreateOperatorClient();
        var pattern = await SeededPatternAsync(client);
        var day = Day();

        using var barrier = new Barrier(RacingWriters);
        var week = ReplaceRulesWhenReleasedAsync(client, pattern, Wednesday, barrier);
        var date = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await SaveOverrideAsync(
                client, day, [new { startMinute = NineAm, endMinute = Noon }]);
        });

        var answers = await Task.WhenAll(week, date);

        answers.ShouldAllBe(answer => answer.StatusCode == HttpStatusCode.OK);
    }

    /// <summary>
    /// The name is how the operator picks a pattern out of a list, so two patterns wearing one
    /// name attaches the wrong availability to a link. The refusal comes from the unique index by
    /// way of <see cref="ConcurrentWrite"/> rather than a SELECT this handler runs first, which
    /// is what makes it hold under a race and why clients branch on the code. What the list holds
    /// afterwards is asserted too: a 409 over a row that landed anyway is the duplicate wearing
    /// the refusal's clothes.
    /// </summary>
    [Fact]
    public async Task CreatePattern_WithANameAlreadyTaken_IsRefusedWithTheCode()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();
        var first = await CreatePatternWithMondayNineToFiveAsync(client);

        var response = await client.PostAsJsonAsync(
            $"{ApiVersions.V1}/admin/patterns",
            new { name = first.Name },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(response)).ShouldBe(PatternsAdminEndpoints.PatternNameTakenCode);

        var after = await client.GetFromJsonAsync<IReadOnlyList<PatternResponse>>(
            $"{ApiVersions.V1}/admin/patterns", TestContext.Current.CancellationToken);
        after!.Where(candidate => candidate.Name == first.Name)
            .ShouldHaveSingleItem().Id.ShouldBe(first.Id);
    }

    /// <summary>
    /// The same collision by the other route, uncovered by the create above: a rename goes
    /// through ExecuteUpdate, which never loads the row, so it is a second write path to the one
    /// index. The name afterwards is asserted too — otherwise the refusal is only a status line.
    /// </summary>
    [Fact]
    public async Task RenamePattern_OntoANameAlreadyTaken_IsRefusedWithTheCode()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();
        var first = await CreatePatternWithMondayNineToFiveAsync(client);

        var second = await PatternOfAsync(await client.PostAsJsonAsync(
            $"{ApiVersions.V1}/admin/patterns",
            new { name = SecondPatternName },
            TestContext.Current.CancellationToken));

        var response = await client.PatchAsJsonAsync(
            $"{ApiVersions.V1}/admin/patterns/{second.Id}",
            new { name = first.Name },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(response)).ShouldBe(PatternsAdminEndpoints.PatternNameTakenCode);

        var after = await client.GetFromJsonAsync<IReadOnlyList<PatternResponse>>(
            $"{ApiVersions.V1}/admin/patterns", TestContext.Current.CancellationToken);
        after!.Single(candidate => candidate.Id == second.Id).Name.ShouldBe(SecondPatternName);
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
            new { name = FirstPatternName },
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

    /// <summary>One writer, held at the barrier until the other is ready to send too.</summary>
    private static Task<HttpResponseMessage> ReplaceRulesWhenReleasedAsync(
        HttpClient client,
        PatternResponse pattern,
        int isoDayOfWeek,
        Barrier barrier) =>
        Task.Run(() =>
        {
            barrier.SignalAndWait();
            return ReplaceRulesAsync(
                client,
                pattern,
                [new { isoDayOfWeek, startMinute = NineAm, endMinute = FivePm }]);
        });

    /// <summary>The seeded pattern read back through the list, for what a refused write left.</summary>
    private static Task<PatternResponse> SeededPatternAsync(HttpClient client) =>
        StoredPatternAsync(client, PatternId);

    /// <summary>
    /// The same read for a pattern a test created rather than seeded. Through the list rather
    /// than through the write's own answer, because what a refused write left behind is only
    /// visible in a read that did not take part in it.
    /// </summary>
    private static async Task<PatternResponse> StoredPatternAsync(HttpClient client, Guid patternId)
    {
        var patterns = await client.GetFromJsonAsync<IReadOnlyList<PatternResponse>>(
            $"{ApiVersions.V1}/admin/patterns", TestContext.Current.CancellationToken);

        return patterns!.Single(candidate => candidate.Id == patternId);
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
        context.Patterns.Add(new Pattern { Id = PatternId, Name = FirstPatternName });
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

    /// <summary>The other tab's DELETE, landed once, just before the next save writes.</summary>
    private sealed class RemoveDateBeforeSave(string connectionString, Guid patternId, DateOnly date)
        : SaveChangesInterceptor
    {
        private bool _removed;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_removed)
            {
                _removed = true;
                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                SqlitePragmas.Apply(connection);
                await using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM PatternOverrides WHERE PatternId = $patternId AND Date = $date";
                // Upper case, as EF's SQLite provider stores a Guid; any other spelling matches nothing.
                command.Parameters.AddWithValue("$patternId", patternId.ToString().ToUpperInvariant());
                command.Parameters.AddWithValue("$date", date.ToString(StoredDateFormat, CultureInfo.InvariantCulture));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
