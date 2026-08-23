using System.ComponentModel.DataAnnotations;

using Kairos.Api.Availability;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Shared;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

using NodaTime.Text;

namespace Kairos.Api.Features.Admin;

/// <summary>The name, and nothing else. Create and rename are the same one-field form.</summary>
public sealed record SavePatternRequest
{
    [Required]
    [StringLength(MaxNameLength)]
    public required string Name { get; init; }

    private const int MaxNameLength = 100;
}

/// <summary>
/// One weekly window. The bounds are the ones <see cref="AvailabilityQuery"/> already refuses to
/// expand, declared here so a window that cannot be read is never written in the first place.
/// </summary>
public sealed record SaveRuleRequest
{
    /// <summary>ISO-8601: 1 is Monday, 7 is Sunday. Not <see cref="DayOfWeek"/>, whose Sunday is 0.</summary>
    [Range(Monday, Sunday)]
    public required int IsoDayOfWeek { get; init; }

    /// <summary>Minutes from local midnight.</summary>
    [Range(FirstMinuteOfDay, AvailabilityQuery.LastMinuteOfDay)]
    public required int StartMinute { get; init; }

    [Range(FirstMinuteOfDay, AvailabilityQuery.LastMinuteOfDay)]
    public required int EndMinute { get; init; }

    private const int Monday = 1;
    private const int Sunday = 7;
    private const int FirstMinuteOfDay = 0;
}

/// <summary>A day absent from the list is a closed day. There is no separate closed flag.</summary>
public sealed record ReplaceRulesRequest
{
    [Required]
    [MaxLength(PatternsAdminEndpoints.MaxWindowsPerWeek)]
    public required IReadOnlyList<SaveRuleRequest> Rules { get; init; }
}

/// <summary>
/// One window a date offers. No day of the week and no date: the date is the route, so a window
/// here is only its hours.
/// </summary>
public sealed record OverrideWindowRequest
{
    /// <summary>Minutes from local midnight.</summary>
    [Range(FirstMinuteOfDay, AvailabilityQuery.LastMinuteOfDay)]
    public required int StartMinute { get; init; }

    [Range(FirstMinuteOfDay, AvailabilityQuery.LastMinuteOfDay)]
    public required int EndMinute { get; init; }

    private const int FirstMinuteOfDay = 0;
}

/// <summary>
/// What a date offers instead of its weekly rules. An empty list blocks it; DELETE on the same
/// route puts the date back on the weekly rules, which is a different thing.
/// </summary>
public sealed record SaveOverrideRequest
{
    [Required]
    [MaxLength(PatternsAdminEndpoints.MaxWindowsPerWeek)]
    public required IReadOnlyList<OverrideWindowRequest> Windows { get; init; }
}

public sealed record PatternRuleResponse(Guid Id, int IsoDayOfWeek, int StartMinute, int EndMinute);

public sealed record OverrideWindowResponse(int StartMinute, int EndMinute);

/// <summary>
/// Keyed by date rather than by id: the operator's route to it is the day they are thinking about,
/// not an identifier they would have to look up. A date with an empty <see cref="Windows"/> is
/// blocked; a date absent from the list is still on the weekly rules, and telling those apart is
/// why a date that offers nothing keeps a row.
/// </summary>
public sealed record PatternOverrideResponse(
    DateOnly Date,
    IReadOnlyList<OverrideWindowResponse> Windows);

/// <summary>
/// A pattern whole: its weekly shape, its exceptions, and the links standing on it.
/// <see cref="UsedBy"/> is the same list the refused delete names, sent up front so the operator
/// reads the consequence before pressing the button rather than after.
/// </summary>
public sealed record PatternResponse(
    Guid Id,
    string Name,
    IReadOnlyList<PatternRuleResponse> Rules,
    IReadOnlyList<PatternOverrideResponse> Overrides,
    IReadOnlyList<string> UsedBy);

/// <summary>
/// <b>A pattern is a shape, not a schedule.</b> Nothing here reads a booking or writes a link —
/// which pattern a link stands on is the link's own field. Both have been proposed for this screen,
/// and both turn a reusable object into a diary of one.
///
/// <b>Every write answers with the whole pattern.</b> One extra read per mutation buys a client
/// that replaces the card rather than merging a partial response into a cached one.
/// </summary>
public static class PatternsAdminEndpoints
{
    /// <summary>
    /// The pattern is a link's availability, so deleting it is refused and the links are named.
    /// A cascade would leave those links resolvable, bookable and offering nothing, which reads
    /// as an outage rather than as a deletion somebody chose.
    /// </summary>
    public const string PatternInUseCode = "pattern_in_use";

    /// <summary>
    /// Two patterns with one name. The name is how the operator picks one, and the unique index
    /// is what this reports rather than duplicating in a pre-check that a second request races.
    /// </summary>
    public const string PatternNameTakenCode = "pattern_name_taken";

    /// <summary>A name of nothing but whitespace. Refused after trimming, not before.</summary>
    public const string PatternNameRequiredCode = "pattern_name_required";

    /// <summary>
    /// Two windows on one day that overlap — a weekday's or one date's, since both go through this
    /// check. They generate the same candidate start twice, which the booking page shows as a
    /// duplicated time. Windows that merely touch are not an overlap: 09:00-12:00 beside
    /// 12:00-17:00 is a contiguous day and generates each start once.
    /// </summary>
    public const string RuleOverlapsCode = "rule_overlaps";

    /// <summary>End is not after start. Shared by rules and overrides — one meaning, one code.</summary>
    public const string WindowInvertedCode = "window_inverted";

    /// <summary>
    /// Two writers on one pattern, interleaving — two admin tabs, or one tab and a forgotten one.
    /// Named rather than left as a 500: the operator's answer is "reload and look at what the other
    /// one did", which a stack trace does not say.
    /// </summary>
    /// <remarks>
    /// The pattern rather than the week or the date, because one code is raised by two guards: a
    /// rules save refused when another replaced the week under it, and a date's save refused by any
    /// save of any date on the same pattern, since that lock is the pattern's. Naming either would
    /// point the operator at something nothing touched.
    /// </remarks>
    public const string PatternChangedCode = "pattern_changed";

    /// <summary>
    /// The <c>{date}</c> segment of an override route is not an ISO calendar date. Thrown from the
    /// handler rather than declared as a route constraint because no regular expression rejects
    /// 2026-02-30 and the parse has to run anyway. Left to the binder it was a
    /// <c>BadHttpRequestException</c>: a 500 in Development, where ThrowOnBadRequest defaults on,
    /// and an empty 400 elsewhere, where every other malformed input answers a coded 400.
    /// </summary>
    public const string DateInvalidCode = "date_invalid";

    /// <summary>
    /// More windows on one day than a day may hold. Coded rather than left to the bound on the
    /// request, so the same mistake answers the same way whether it arrives as a weekday's rules
    /// or as one date's: an attribute counts a whole body and cannot tell one day's share from
    /// another's, and a client branching on <c>code</c> would otherwise see two different answers.
    /// </summary>
    public const string TooManyWindowsCode = "too_many_windows";

    /// <summary>
    /// More windows on one day than a day is ever shaped like. Six is past a morning, an
    /// afternoon and an evening with room to spare, while the overlap check on its own tolerates
    /// 1,439 — one-minute windows end to end, across a day whose last storable minute is 1,439.
    /// </summary>
    public const int MaxWindowsPerDate = 6;

    public const int DaysInWeek = 7;

    /// <summary>
    /// A full week of full days, and so the most windows any body here could carry. The allocation
    /// guard on both write bodies — an array the size of Kestrel's 30 MB cap is refused as it binds,
    /// not after being projected and sorted — and deliberately not the domain rule, which
    /// <see cref="ValidateDay"/> owns: a bound on a whole body cannot tell one day's share from
    /// another's.
    /// </summary>
    public const int MaxWindowsPerWeek = MaxWindowsPerDate * DaysInWeek;

    private const string PatternName = "Pattern";
    private const string OverrideName = "Date override";

    private const string DateParameter = "date";

    /// <summary>DateOnly holds no year before this; NodaTime's ISO pattern reaches back past it.</summary>
    private const int FirstYearDateOnlyHolds = 1;

    private const string NameTaken = "Another pattern already has that name.";

    private const string WindowInverted = "A window has to end after it starts.";

    private const string PatternChanged =
        "The pattern was changed somewhere else. Reload and try again.";

    private const string RuleOverlaps =
        "Another window on that day already covers part of those hours. Overlapping windows offer "
        + "the same time twice.";

    /// <summary>
    /// Interpolated at type load rather than written as a literal, so the number in the message
    /// cannot drift from the number that is enforced. A const string cannot carry a const int
    /// placeholder, which is why this is static readonly.
    /// </summary>
    private static readonly string TooManyWindows =
        $"A day can offer at most {MaxWindowsPerDate} windows.";

    public static RouteGroupBuilder MapPatternsAdmin(this RouteGroupBuilder group)
    {
        group.MapGet("/patterns", ListAsync).WithName("ListPatterns");
        group.MapPost("/patterns", CreateAsync).WithName("CreatePattern");
        group.MapPatch("/patterns/{patternId:guid}", RenameAsync).WithName("RenamePattern");
        group.MapDelete("/patterns/{patternId:guid}", DeleteAsync).WithName("DeletePattern");

        group.MapPut("/patterns/{patternId:guid}/rules", ReplaceRulesAsync)
            .WithName("ReplacePatternRules");

        // Keyed on the date, so it is a replacement rather than a create-or-update the caller has
        // to choose between: setting Christmas twice leaves the second request's windows and only
        // those, whatever the date held before.
        group.MapPut("/patterns/{patternId:guid}/overrides/{date}", SaveOverrideAsync)
            .WithName("SavePatternOverride");
        group.MapDelete("/patterns/{patternId:guid}/overrides/{date}", RemoveOverrideAsync)
            .WithName("RemovePatternOverride");

        return group;
    }

    private static async Task<IReadOnlyList<PatternResponse>> ListAsync(
        KairosDbContext context,
        CancellationToken cancellationToken)
    {
        var patterns = await context.Patterns.AsNoTracking()
            .Include(pattern => pattern.Rules)
            .Include(pattern => pattern.Overrides)
            .OrderBy(pattern => pattern.Name)
            .ToListAsync(cancellationToken);

        // One query for every link rather than one per pattern. Links has no inverse navigation
        // — nothing else in the application asks a pattern for its links — so the join is made
        // here instead of being added to the model for this one screen.
        var links = await context.Links.AsNoTracking()
            .Select(link => new { link.PatternId, link.Name })
            .ToListAsync(cancellationToken);

        var usedBy = links.ToLookup(link => link.PatternId, link => link.Name);

        return [.. patterns.Select(pattern => Describe(pattern, [.. usedBy[pattern.Id].Order()]))];
    }

    private static async Task<Created<PatternResponse>> CreateAsync(
        SavePatternRequest request,
        KairosDbContext context,
        CancellationToken cancellationToken)
    {
        var pattern = new Pattern { Name = Named(request) };
        context.Patterns.Add(pattern);
        await ConcurrentWrite.NamedAsync(
            () => context.SaveChangesAsync(cancellationToken), PatternNameTakenCode, NameTaken);

        // A pattern with no rules yet, which is the only thing a create can produce: the weekly
        // shape is written afterwards, and a form that demanded one before the pattern existed
        // would be a form that cannot be abandoned halfway.
        return TypedResults.Created(
            $"{ApiVersions.V1}/admin/patterns/{pattern.Id}",
            await ReadAsync(context, pattern.Id, cancellationToken));
    }

    private static async Task<PatternResponse> RenameAsync(
        Guid patternId,
        SavePatternRequest request,
        KairosDbContext context,
        CancellationToken cancellationToken)
    {
        // Evaluated before the expression tree is built. Inlining the call would put a method EF
        // cannot translate inside the SetProperty lambda, and the trim would never run.
        var name = Named(request);

        var renamed = await ConcurrentWrite.NamedAsync(
            () => context.Patterns
                .Where(pattern => pattern.Id == patternId)
                .ExecuteUpdateAsync(
                    pattern => pattern.SetProperty(candidate => candidate.Name, name),
                    cancellationToken),
            PatternNameTakenCode,
            NameTaken);

        if (renamed == 0)
        {
            throw DomainException.NotFound(PatternName);
        }

        return await ReadAsync(context, patternId, cancellationToken);
    }

    private static async Task<NoContent> DeleteAsync(
        Guid patternId,
        KairosDbContext context,
        CancellationToken cancellationToken)
    {
        var usedBy = await UsedByAsync(context, patternId, cancellationToken);
        if (usedBy.Count > 0)
        {
            // Named, not counted. "2 links use this" sends the operator hunting through the links
            // table for which two; the answer is one query away here and nowhere else.
            throw DomainException.Conflict(
                PatternInUseCode,
                $"This pattern is the availability of {string.Join(", ", usedBy)}. Point "
                + $"{(usedBy.Count == 1 ? "that link" : "those links")} at another pattern first.");
        }

        var deleted = await context.Patterns
            .Where(pattern => pattern.Id == patternId)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted == 0)
        {
            throw DomainException.NotFound(PatternName);
        }

        return TypedResults.NoContent();
    }

    private static async Task<PatternResponse> ReplaceRulesAsync(
        Guid patternId,
        ReplaceRulesRequest request,
        KairosDbContext context,
        CancellationToken cancellationToken)
    {
        var pattern = await context.Patterns
            .Include(candidate => candidate.Rules)
            .SingleOrDefaultAsync(candidate => candidate.Id == patternId, cancellationToken)
            ?? throw DomainException.NotFound(PatternName);

        foreach (var day in request.Rules.GroupBy(rule => rule.IsoDayOfWeek))
        {
            ValidateDay(day.Select(rule => (Start: rule.StartMinute, End: rule.EndMinute)));
        }

        // The week as it stood when this request read it. Everything below turns "somebody else
        // replaced the week while this operator was choosing" into a refusal rather than into a
        // merge of the two, and it does so by hand rather than by an EF concurrency token — see
        // Pattern.RulesVersion for why a second token would be the wrong instrument.
        var seenRulesVersion = pattern.RulesVersion;

        context.PatternRules.RemoveRange(pattern.Rules);
        foreach (var rule in request.Rules)
        {
            pattern.Rules.Add(new PatternRule
            {
                PatternId = pattern.Id,
                IsoDayOfWeek = rule.IsoDayOfWeek,
                StartMinute = rule.StartMinute,
                EndMinute = rule.EndMinute,
            });
        }

        // The same budget BookingWriter takes, and for the same reason: busy_timeout does not bound
        // a contended write, because Microsoft.Data.Sqlite retries SQLITE_BUSY until the command
        // timeout, thirty seconds by default. Measured on this stack — a writer blocked behind the
        // transaction below waited 31s unbounded and 5s with this line, and
        // DatabaseBusyExceptionHandler tells the caller to come back in 5. Second place to hold the
        // write lock, so second place to say how long it will wait for it.
        context.Database.SetCommandTimeout(
            TimeSpan.FromMilliseconds(SqlitePragmas.BusyTimeoutMilliseconds));

        await using var write = await context.Database.BeginTransactionAsync(cancellationToken);

        // The gate, and it runs before a rule row is touched. ExecuteUpdate bypasses the change
        // tracker and is generated from this predicate alone, so it is unaffected by the token on
        // OverridesVersion — which is the whole reason the two are separate columns.
        var claimed = await context.Patterns
            .Where(row => row.Id == pattern.Id && row.RulesVersion == seenRulesVersion)
            .ExecuteUpdateAsync(
                update => update.SetProperty(row => row.RulesVersion, row => row.RulesVersion + 1),
                cancellationToken);

        if (claimed == 0)
        {
            // Another writer replaced this week between the read and here. Refused before a
            // single rule row is written rather than written and rolled back: a week half one
            // operator's and half another's is a shape neither asked for, and is what this route
            // used to produce on a pattern that had no rules to collide over.
            throw DomainException.Conflict(PatternChangedCode, PatternChanged);
        }

        // No ConcurrentWrite here, unlike every other write in this file, and the omission is
        // measured rather than assumed. The transaction above holds the write lock from its BEGIN —
        // a second connection's insert waits and then reports "database is locked" — so no other
        // writer's delete can land between the gate and this line; the gate proves nothing moved
        // before it; and PatternRules carries no unique index for the other branch to catch.
        await context.SaveChangesAsync(cancellationToken);

        await write.CommitAsync(cancellationToken);

        return await ReadAsync(context, pattern.Id, cancellationToken);
    }

    private static async Task<PatternResponse> SaveOverrideAsync(
        Guid patternId,
        string date,
        SaveOverrideRequest request,
        KairosDbContext context,
        CancellationToken cancellationToken)
    {
        // Parsed once, here, and everything below works in DateOnly as it always did — threading
        // the raw segment further down would put the same parse in front of every use of it.
        var day = ParseDate(date);

        var pattern = await context.Patterns
            .Include(candidate => candidate.Overrides)
            .SingleOrDefaultAsync(candidate => candidate.Id == patternId, cancellationToken)
            ?? throw DomainException.NotFound(PatternName);

        ValidateDay(request.Windows.Select(window => (Start: window.StartMinute, End: window.EndMinute)));

        // Materialized before anything is added, so the removal is not walking the collection the
        // additions are growing.
        var replaced = pattern.Overrides.Where(row => row.Date == day).ToList();
        context.PatternOverrides.RemoveRange(replaced);

        if (request.Windows.Count == 0)
        {
            // One row with no times, rather than no rows at all: no rows is the date back on the
            // weekly rules, which is what DELETE on this route means and not what was asked for.
            pattern.Overrides.Add(new PatternOverride { PatternId = pattern.Id, Date = day });
        }

        foreach (var window in request.Windows)
        {
            pattern.Overrides.Add(new PatternOverride
            {
                PatternId = pattern.Id,
                Date = day,
                StartMinute = window.StartMinute,
                EndMinute = window.EndMinute,
            });
        }

        // The whole of what makes two tabs on one date safe. The unique index in
        // PatternOverrideConfiguration catches only an identical pair, and the tracked delete only
        // raises where there were rows to delete, so a date that held nothing yet let both writers
        // insert and kept the union — neither operator's answer. Raising the number they both read
        // means EF writes WHERE OverridesVersion = n, the second one matches nothing, and the loser
        // is told.
        pattern.OverridesVersion++;

        await ConcurrentWrite.NamedAsync(
            async () =>
            {
                await context.SaveChangesAsync(cancellationToken);
                return true;
            },
            PatternChangedCode,
            PatternChanged);

        return await ReadAsync(context, pattern.Id, cancellationToken);
    }

    private static async Task<PatternResponse> RemoveOverrideAsync(
        Guid patternId,
        string date,
        KairosDbContext context,
        CancellationToken cancellationToken)
    {
        var day = ParseDate(date);

        var deleted = await context.PatternOverrides
            .Where(row => row.PatternId == patternId && row.Date == day)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted == 0)
        {
            throw DomainException.NotFound(OverrideName);
        }

        // The date goes back to the weekly rules, which is not blocking the day — hence two
        // requests. Deliberately not raising OverridesVersion, unlike the save above: the token
        // guards a read-modify-write and this has none. ExecuteDelete decides on rows the database
        // matched, so two racing answer coherently — one deletes, the other is told the override
        // does not exist, which by then is true. Bumping would load the pattern to write a column
        // nothing reads here, and make a remove on one date refuse a save on an unrelated one.
        return await ReadAsync(context, patternId, cancellationToken);
    }

    private static async Task<PatternResponse> ReadAsync(
        KairosDbContext context,
        Guid patternId,
        CancellationToken cancellationToken)
    {
        var pattern = await context.Patterns.AsNoTracking()
            .Include(candidate => candidate.Rules)
            .Include(candidate => candidate.Overrides)
            .FirstOrDefaultAsync(candidate => candidate.Id == patternId, cancellationToken)
            ?? throw DomainException.NotFound(PatternName);

        return Describe(pattern, await UsedByAsync(context, patternId, cancellationToken));
    }

    private static PatternResponse Describe(Pattern pattern, IReadOnlyList<string> usedBy) =>
        new(
            pattern.Id,
            pattern.Name,
            [.. pattern.Rules
                .OrderBy(rule => rule.IsoDayOfWeek)
                .ThenBy(rule => rule.StartMinute)
                .Select(rule => new PatternRuleResponse(
                    rule.Id, rule.IsoDayOfWeek, rule.StartMinute, rule.EndMinute))],
            [.. pattern.Overrides
                .GroupBy(row => row.Date)
                .OrderBy(date => date.Key)
                .Select(date => new PatternOverrideResponse(date.Key, [.. WindowsOf(date)]))],
            usedBy);

    /// <summary>
    /// A date's windows, mornings first. The row with no times is not one of them — it is the
    /// date being blocked, and the empty list it leaves behind is what says so.
    /// </summary>
    private static IEnumerable<OverrideWindowResponse> WindowsOf(IEnumerable<PatternOverride> rows)
    {
        foreach (var row in rows.OrderBy(candidate => candidate.StartMinute))
        {
            if (row.StartMinute is { } start && row.EndMinute is { } end)
            {
                yield return new OverrideWindowResponse(start, end);
            }
        }
    }

    /// <summary>
    /// The <c>{date}</c> segment, in the entity's own type. Read through NodaTime's ISO pattern
    /// rather than by the binder, so the route accepts the one spelling the rest of this API speaks
    /// — the pattern <see cref="Slots.SlotsEndpoints"/> and <see cref="MeetingsAdminEndpoints"/>
    /// use. The caller's value is not echoed back into the message, for the reason those two state.
    /// </summary>
    /// <remarks>
    /// A successful parse is not enough to convert: the pattern reads years the stored type cannot
    /// hold, and a year below it threw <c>ArgumentOutOfRangeException</c>, which the handler leaves
    /// alone, so the refusal happens here to stay a coded 400. The far end needs no guard — the
    /// pattern's four-digit year already refuses anything past 9999.
    /// </remarks>
    private static DateOnly ParseDate(string value)
    {
        var parsed = LocalDatePattern.Iso.Parse(value);
        if (!parsed.Success || parsed.Value.Year < FirstYearDateOnlyHolds)
        {
            throw DomainException.Invalid(
                DateInvalidCode, $"'{DateParameter}' must be an ISO calendar date, such as 2026-09-07.");
        }

        return parsed.Value.ToDateOnly();
    }

    /// <summary>
    /// One day's windows — a weekday's or one date's — checked before anything is written, so the
    /// set is refused entire or stored entire.
    /// </summary>
    private static void ValidateDay(IEnumerable<(int Start, int End)> day)
    {
        // Half-open windows: sorted by start, an overlap is a window starting before the previous
        // one ends. Written this way so two that merely touch at noon are a split day rather than
        // a refusal.
        var windows = day.OrderBy(window => window.Start).ToList();

        // Counted here rather than on the request, because the attribute bounds a whole body and
        // this bounds one day of it — the rule the operator actually has, and the one they get a
        // code for.
        if (windows.Count > MaxWindowsPerDate)
        {
            throw DomainException.Invalid(TooManyWindowsCode, TooManyWindows);
        }

        foreach (var (start, end) in windows)
        {
            if (end <= start)
            {
                throw DomainException.Invalid(WindowInvertedCode, WindowInverted);
            }
        }

        for (var index = 1; index < windows.Count; index++)
        {
            if (windows[index].Start < windows[index - 1].End)
            {
                throw DomainException.Conflict(RuleOverlapsCode, RuleOverlaps);
            }
        }
    }

    private static async Task<IReadOnlyList<string>> UsedByAsync(
        KairosDbContext context,
        Guid patternId,
        CancellationToken cancellationToken) =>
        await context.Links.AsNoTracking()
            .Where(link => link.PatternId == patternId)
            .OrderBy(link => link.Name)
            .Select(link => link.Name)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The name as it will be stored. Trimmed first, so " " is refused rather than saved as a
    /// pattern nobody can see the name of, and so two names differing only in spacing collide on
    /// the unique index instead of both existing.
    /// </summary>
    private static string Named(SavePatternRequest request)
    {
        var name = request.Name.Trim();
        if (name.Length == 0)
        {
            throw DomainException.Invalid(PatternNameRequiredCode, "A pattern needs a name.");
        }

        return name;
    }
}
