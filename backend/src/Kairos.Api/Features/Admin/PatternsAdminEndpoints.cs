using System.ComponentModel.DataAnnotations;

using Kairos.Api.Availability;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Shared;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

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

/// <summary>
/// What one date does instead of the weekly rules: different times, or — with both fields absent
/// — nothing at all, which is how a holiday is expressed. The two nulls are the whole reason this
/// is not a copy of <see cref="SaveRuleRequest"/> with an extra date on it.
/// </summary>
public sealed record SaveOverrideRequest
{
    [Range(FirstMinuteOfDay, AvailabilityQuery.LastMinuteOfDay)]
    public int? StartMinute { get; init; }

    [Range(FirstMinuteOfDay, AvailabilityQuery.LastMinuteOfDay)]
    public int? EndMinute { get; init; }

    private const int FirstMinuteOfDay = 0;
}

public sealed record PatternRuleResponse(Guid Id, int IsoDayOfWeek, int StartMinute, int EndMinute);

/// <summary>
/// Keyed by date rather than by id, because the date is what identifies it: the unique index on
/// (PatternId, Date) means one date has at most one of these, so the operator's route to it is
/// the day they are thinking about and never an identifier they would have to look up first.
/// </summary>
public sealed record PatternOverrideResponse(DateOnly Date, int? StartMinute, int? EndMinute);

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
/// The operator's availability: patterns, their weekly rules and their date overrides.
/// </summary>
/// <remarks>
/// <para>
/// <b>A pattern is a shape, not a schedule.</b> Nothing here reads a booking and nothing here
/// writes a link. Which pattern a link stands on is the link's field, edited where links are
/// edited; a booking is something that already happened to a shape. Both have been proposed for
/// this screen and both would turn a reusable object into a diary of one.
/// </para>
/// <para>
/// <b>Every write answers with the whole pattern.</b> One extra read per mutation buys a client
/// that never merges a partial response into a cached one — the card is replaced, not patched —
/// and for a single-operator workbench that read costs nothing worth counting.
/// </para>
/// </remarks>
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
    /// Two windows on one day that overlap. Overlapping windows generate the same candidate start
    /// twice, which surfaces on the booking page as a duplicated time — a data bug wearing a
    /// rendering bug's clothes. Windows that merely touch are not an overlap: 09:00-12:00 beside
    /// 12:00-17:00 is a contiguous day and generates each start once.
    /// </summary>
    public const string RuleOverlapsCode = "rule_overlaps";

    /// <summary>End is not after start. Shared by rules and overrides — one meaning, one code.</summary>
    public const string WindowInvertedCode = "window_inverted";

    /// <summary>
    /// An override with a start and no end, or an end and no start. Both absent is the blocked
    /// day and is the point of the endpoint; one absent is a half-filled form.
    /// </summary>
    public const string OverrideIncompleteCode = "override_incomplete";

    /// <summary>
    /// Two writes set the same date at once. Only reachable from two tabs or two windows, which
    /// on a single-operator scheduler is rare and not impossible — and the alternative to naming
    /// it is a 500 with a SQLite constraint in the body.
    /// </summary>
    public const string OverrideDateTakenCode = "override_date_taken";

    private const string PatternName = "Pattern";
    private const string RuleName = "Rule";
    private const string OverrideName = "Date override";

    private const string NameTaken = "Another pattern already has that name.";

    private const string DateTaken =
        "That date was set by another change a moment ago. Reload the pattern and set it again.";

    public static RouteGroupBuilder MapPatternsAdmin(this RouteGroupBuilder group)
    {
        group.MapGet("/patterns", ListAsync).WithName("ListPatterns");
        group.MapPost("/patterns", CreateAsync).WithName("CreatePattern");
        group.MapPatch("/patterns/{patternId:guid}", RenameAsync).WithName("RenamePattern");
        group.MapDelete("/patterns/{patternId:guid}", DeleteAsync).WithName("DeletePattern");

        group.MapPost("/patterns/{patternId:guid}/rules", AddRuleAsync).WithName("AddPatternRule");
        group.MapDelete("/patterns/{patternId:guid}/rules/{ruleId:guid}", RemoveRuleAsync)
            .WithName("RemovePatternRule");

        // Keyed on the date, so it is an upsert rather than a create-or-update the caller has to
        // choose between: setting Christmas twice is one row either way.
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
        await UniquelyNamedAsync(
            () => context.SaveChangesAsync(cancellationToken), PatternNameTakenCode, NameTaken);

        // A pattern with no rules yet, which is the only thing a create can produce: the weekly
        // windows are added one at a time, and a form that demanded one before the pattern
        // existed would be a form that cannot be abandoned halfway.
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

        var renamed = await UniquelyNamedAsync(
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

    private static async Task<PatternResponse> AddRuleAsync(
        Guid patternId,
        SaveRuleRequest request,
        KairosDbContext context,
        CancellationToken cancellationToken)
    {
        if (request.EndMinute <= request.StartMinute)
        {
            throw DomainException.Invalid(
                WindowInvertedCode, "A window has to end after it starts.");
        }

        if (!await ExistsAsync(context, patternId, cancellationToken))
        {
            throw DomainException.NotFound(PatternName);
        }

        var sameDay = await context.PatternRules.AsNoTracking()
            .Where(rule => rule.PatternId == patternId && rule.IsoDayOfWeek == request.IsoDayOfWeek)
            .Select(rule => new { rule.StartMinute, rule.EndMinute })
            .ToListAsync(cancellationToken);

        // Half-open windows: [a,b) and [c,d) overlap when a < d and c < b. Written this way so
        // two windows that merely touch at noon are a split day rather than a refusal.
        if (sameDay.Any(rule =>
            rule.StartMinute < request.EndMinute && request.StartMinute < rule.EndMinute))
        {
            throw DomainException.Conflict(
                RuleOverlapsCode,
                "Another window on that day already covers part of those hours. Overlapping "
                + "windows offer the same time twice.");
        }

        context.PatternRules.Add(new PatternRule
        {
            PatternId = patternId,
            IsoDayOfWeek = request.IsoDayOfWeek,
            StartMinute = request.StartMinute,
            EndMinute = request.EndMinute,
        });
        await context.SaveChangesAsync(cancellationToken);

        return await ReadAsync(context, patternId, cancellationToken);
    }

    private static async Task<PatternResponse> RemoveRuleAsync(
        Guid patternId,
        Guid ruleId,
        KairosDbContext context,
        CancellationToken cancellationToken)
    {
        var deleted = await context.PatternRules
            .Where(rule => rule.PatternId == patternId && rule.Id == ruleId)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted == 0)
        {
            throw DomainException.NotFound(RuleName);
        }

        return await ReadAsync(context, patternId, cancellationToken);
    }

    private static async Task<PatternResponse> SaveOverrideAsync(
        Guid patternId,
        DateOnly date,
        SaveOverrideRequest request,
        KairosDbContext context,
        CancellationToken cancellationToken)
    {
        var start = request.StartMinute;
        var end = request.EndMinute;

        if ((start is null) != (end is null))
        {
            throw DomainException.Invalid(
                OverrideIncompleteCode,
                "A date override needs both times or neither. Neither is the day being blocked "
                + "entirely; one on its own is a form somebody stopped halfway through.");
        }

        if (start is { } from && end is { } to && to <= from)
        {
            throw DomainException.Invalid(
                WindowInvertedCode, "A window has to end after it starts.");
        }

        if (!await ExistsAsync(context, patternId, cancellationToken))
        {
            throw DomainException.NotFound(PatternName);
        }

        var replaced = await context.PatternOverrides
            .Where(row => row.PatternId == patternId && row.Date == date)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(candidate => candidate.StartMinute, start)
                    .SetProperty(candidate => candidate.EndMinute, end),
                cancellationToken);

        if (replaced == 0)
        {
            context.PatternOverrides.Add(new PatternOverride
            {
                PatternId = patternId,
                Date = date,
                StartMinute = start,
                EndMinute = end,
            });

            // Wrapped for the same reason the name writes are, and against the same index. Two
            // PUTs for one date both read replaced == 0 and both take this branch; the second one
            // hits the unique index on (PatternId, Date), and unwrapped that arrives as a 500
            // about a constraint rather than as the conflict it is.
            await UniquelyNamedAsync(
                () => context.SaveChangesAsync(cancellationToken), OverrideDateTakenCode, DateTaken);
        }

        return await ReadAsync(context, patternId, cancellationToken);
    }

    private static async Task<PatternResponse> RemoveOverrideAsync(
        Guid patternId,
        DateOnly date,
        KairosDbContext context,
        CancellationToken cancellationToken)
    {
        var deleted = await context.PatternOverrides
            .Where(row => row.PatternId == patternId && row.Date == date)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted == 0)
        {
            throw DomainException.NotFound(OverrideName);
        }

        // The date goes back to the weekly rules. Removing an override is not the same as
        // blocking the day, which is why the two are different requests.
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
                .OrderBy(row => row.Date)
                .Select(row => new PatternOverrideResponse(
                    row.Date, row.StartMinute, row.EndMinute))],
            usedBy);

    private static Task<bool> ExistsAsync(
        KairosDbContext context,
        Guid patternId,
        CancellationToken cancellationToken) =>
        context.Patterns.AsNoTracking().AnyAsync(pattern => pattern.Id == patternId, cancellationToken);

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

    /// <summary>
    /// A unique index reported as the conflict it is. Caught rather than pre-checked: a SELECT
    /// before the INSERT is a race two browser tabs win often enough to matter, and the index has
    /// to be there for correctness either way.
    /// </summary>
    /// <remarks>
    /// The filter, not the exception type, is what makes this narrow. SaveChanges wraps the
    /// provider failure in DbUpdateException and ExecuteUpdate does not, so a catch written
    /// against either type would silently stop covering one of its callers — and
    /// <see cref="SqliteErrors.IsUniqueViolation"/> already walks the chain to the extended
    /// error code, which is the only thing that means "duplicate" rather than "some constraint".
    /// </remarks>
    private static async Task<int> UniquelyNamedAsync(
        Func<Task<int>> write,
        string code,
        string message)
    {
        try
        {
            return await write();
        }
        catch (Exception failure) when (SqliteErrors.IsUniqueViolation(failure))
        {
            throw DomainException.Conflict(code, message);
        }
    }
}
