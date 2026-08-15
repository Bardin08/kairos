using System.ComponentModel.DataAnnotations;

using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Shared;

using Microsoft.EntityFrameworkCore;

namespace Kairos.Api.Features.Admin;

/// <summary>
/// One link as the workbench edits it, which is not the shape the booking page reads.
/// </summary>
/// <remarks>
/// The four limit columns come back exactly as they sit in the database — <c>null</c> where the
/// link inherits the global, a number where it does not, and <c>0</c> is a number. Resolving them
/// here would be the same defect this whole slice exists to prevent: a link showing 15 looks
/// identical whether it inherited that or had it written in, and once the resolved value has been
/// round-tripped through an editor the link has silently stopped tracking the global forever.
/// </remarks>
public sealed record AdminLinkResponse(
    Guid Id,
    string Slug,
    string Name,
    string Description,
    Guid PatternId,
    string PatternName,
    bool Active,
    IReadOnlyList<int> Durations,
    int? MarginMinutes,
    int? MinNoticeMinutes,
    int? DailyCap,
    int? StartIncrementMinutes);

/// <summary>A pattern as a thing to pick, which is all the link editor needs of one.</summary>
public sealed record AdminPatternResponse(Guid Id, string Name);

/// <summary>
/// The globals, resolved and never null. The editor renders them greyed out inside a disabled
/// input while a limit is inherited, so the operator reads "15" and understands where it came
/// from rather than seeing an empty box.
/// </summary>
public sealed record GlobalLimitsResponse(
    int MarginMinutes,
    int MinNoticeMinutes,
    int DailyCap,
    int StartIncrementMinutes);

/// <summary>
/// Everything the Links page draws, in one read. The patterns are here rather than behind their
/// own route because a link row is not renderable without them — a pattern id is not a name — and
/// the globals are here because a margin cell reading "Global — 15 min" needs the 15.
/// </summary>
public sealed record LinksPageResponse(
    IReadOnlyList<AdminLinkResponse> Links,
    IReadOnlyList<AdminPatternResponse> Patterns,
    GlobalLimitsResponse Globals);

/// <summary>
/// A link's editable shape, for create and update alike. Deliberately not carrying the four
/// limits: they have their own route, whose whole point is that they are nullable in a way these
/// fields are not.
/// </summary>
/// <remarks>
/// <see cref="Active"/> is likewise absent. Archiving is a state transition with its own route
/// rather than a field of a form — one writer per column, so a stale editor cannot resurrect a
/// link somebody archived in another tab.
/// </remarks>
public sealed record SaveLinkRequest
{
    /// <summary>
    /// The path segment the public URL carries, so its alphabet is the URL's rather than a
    /// preference. The read side deliberately has no such pattern — an unusable slug is a 404
    /// there, not a 400 about a regular expression — but a write is where an unreachable link
    /// gets created, and refusing it costs one attribute.
    /// </summary>
    [Required]
    [StringLength(MaxSlugLength)]
    [RegularExpression(SlugPattern)]
    public required string Slug { get; init; }

    [Required]
    [StringLength(MaxNameLength)]
    public required string Name { get; init; }

    [StringLength(MaxDescriptionLength)]
    public string Description { get; init; } = string.Empty;

    [Required]
    public required Guid PatternId { get; init; }

    /// <summary>
    /// In the operator's order, which becomes <see cref="LinkDuration.SortOrder"/> and then the
    /// order of the booking page's tabs. Not sorted here: ascending length is a different list.
    /// </summary>
    [Required]
    public required IReadOnlyList<int> Durations { get; init; }

    public const int MinDurationMinutes = 1;
    public const int MaxDurationMinutes = 1440;

    private const int MaxSlugLength = 80;
    private const int MaxNameLength = 200;
    private const int MaxDescriptionLength = 2000;

    /// <summary>Lowercase words joined by single hyphens. No leading, trailing or doubled ones.</summary>
    private const string SlugPattern = "^[a-z0-9]+(-[a-z0-9]+)*$";
}

/// <summary>
/// All four limits, every time, each nullable. A full replace rather than a patch, and that is
/// the load-bearing decision in this file.
/// </summary>
/// <remarks>
/// Under PATCH semantics "absent" and "explicitly null" are different intentions, and JSON gives
/// no clean way to tell them apart short of JSON Patch — a lot of machinery to distinguish two
/// things a full replace makes unambiguous. Here the body always carries all four keys, so a null
/// is always an instruction and never an omission.
/// </remarks>
public sealed record UpdateLimitsRequest
{
    /// <summary>
    /// Null inherits the global; zero is a deliberate zero, meaning no gap between meetings.
    /// Negative is refused here rather than only by the CHECK constraint, because a negative
    /// margin inverts the padded busy interval and starts *creating* overlaps — which is worse
    /// than a validation error, since it looks like availability.
    /// </summary>
    [Range(MinLimit, int.MaxValue)]
    public int? MarginMinutes { get; init; }

    /// <summary>Zero means bookable immediately.</summary>
    [Range(MinLimit, int.MaxValue)]
    public int? MinNoticeMinutes { get; init; }

    /// <summary>Zero means nothing may be booked that day at all.</summary>
    [Range(MinLimit, int.MaxValue)]
    public int? DailyCap { get; init; }

    /// <summary>
    /// The one of the four with a different floor. Zero is not merely meaningless here, it is the
    /// value that makes the generator's <c>local.PlusMinutes(step)</c> never advance. Part A's
    /// <c>Math.Max(step, 1)</c> catches it defensively inside the loop, but a control that can
    /// send zero at all is a control that will, so the floor belongs at the boundary too.
    /// </summary>
    [Range(MinIncrementMinutes, int.MaxValue)]
    public int? StartIncrementMinutes { get; init; }

    private const int MinLimit = 0;
    private const int MinIncrementMinutes = 1;
}

/// <summary>
/// The operator's write side of links: list, create, update, archive, restore, and the limits
/// replace. Every route hangs off <see cref="AdminGroup.MapAdmin"/> and therefore answers 401 or
/// 403 to an anonymous caller rather than redirecting one — a fetch cannot follow a cross-origin
/// redirect.
/// </summary>
public static class LinksAdminEndpoints
{
    /// <summary>Two links answering to one slug would make which page you get a matter of row order.</summary>
    public const string SlugTakenCode = "slug_taken";

    /// <summary>The pattern id in the body names nothing. Invalid rather than not-found: the link is the resource.</summary>
    public const string PatternUnknownCode = "pattern_unknown";

    /// <summary>
    /// Empty, duplicated, or out of range. One code for all three because a client's recovery is
    /// the same in every case — fix the list and send it again — and the message names which.
    /// </summary>
    public const string DurationsInvalidCode = "durations_invalid";

    public static RouteGroupBuilder MapLinksAdmin(this RouteGroupBuilder group)
    {
        group.MapGet("/links", ListAsync).WithName("ListAdminLinks");
        group.MapPost("/links", CreateAsync).WithName("CreateLink");
        group.MapPut("/links/{id:guid}", UpdateAsync).WithName("UpdateLink");
        group.MapPut("/links/{id:guid}/limits", UpdateLimitsAsync).WithName("UpdateLinkLimits");
        group.MapPost("/links/{id:guid}/archive", ArchiveAsync).WithName("ArchiveLink");
        group.MapPost("/links/{id:guid}/restore", RestoreAsync).WithName("RestoreLink");
        return group;
    }

    /// <summary>
    /// Every link, archived ones included. The public list hides them; this one is where they are
    /// brought back from, so hiding them here would make an archived link unreachable forever.
    /// </summary>
    private static async Task<LinksPageResponse> ListAsync(
        KairosDbContext context,
        CancellationToken cancellationToken)
    {
        var links = await ReadLinksAsync(context, null, cancellationToken);

        var patterns = await context.Patterns.AsNoTracking()
            .OrderBy(pattern => pattern.Name)
            .Select(pattern => new AdminPatternResponse(pattern.Id, pattern.Name))
            .ToListAsync(cancellationToken);

        var settings = await context.Settings.AsNoTracking()
            .SingleAsync(row => row.Id == Settings.SingletonId, cancellationToken);

        return new LinksPageResponse(
            links,
            patterns,
            new GlobalLimitsResponse(
                settings.MarginMinutes,
                settings.MinNoticeMinutes,
                settings.DailyCap,
                settings.StartIncrementMinutes));
    }

    private static async Task<AdminLinkResponse> CreateAsync(
        SaveLinkRequest request,
        KairosDbContext context,
        CancellationToken cancellationToken)
    {
        RequireDurations(request.Durations);
        await RequirePatternAsync(context, request.PatternId, cancellationToken);
        await RequireSlugFreeAsync(context, request.Slug, null, cancellationToken);

        var link = new Link
        {
            Slug = request.Slug,
            Name = request.Name,
            Description = request.Description,
            PatternId = request.PatternId,
        };

        ReplaceDurations(link, request.Durations);
        context.Links.Add(link);
        await context.SaveChangesAsync(cancellationToken);

        return await ReadLinkAsync(context, link.Id, cancellationToken);
    }

    private static async Task<AdminLinkResponse> UpdateAsync(
        Guid id,
        SaveLinkRequest request,
        KairosDbContext context,
        CancellationToken cancellationToken)
    {
        RequireDurations(request.Durations);
        await RequirePatternAsync(context, request.PatternId, cancellationToken);
        await RequireSlugFreeAsync(context, request.Slug, id, cancellationToken);

        // Tracked, and with the durations loaded: replacing the set relies on EF seeing the rows
        // that are going away. Loaded without them, Clear() removes nothing and the link ends up
        // offering the union of every list it has ever had.
        var link = await context.Links
            .Include(candidate => candidate.Durations)
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            ?? throw DomainException.NotFound("Link");

        link.Slug = request.Slug;
        link.Name = request.Name;
        link.Description = request.Description;
        link.PatternId = request.PatternId;
        ReplaceDurations(link, request.Durations);

        await context.SaveChangesAsync(cancellationToken);

        return await ReadLinkAsync(context, id, cancellationToken);
    }

    /// <summary>
    /// The three assignments below are what <see cref="LimitWriteTests"/> exists for, and the one
    /// place Part A's whole global-then-override design could be killed in a single line: the
    /// request's value is assigned across as it arrived, null included, rather than being coalesced
    /// into whatever the link is currently resolving to. Writing the resolved value here would
    /// leave the link looking identical in the UI and silently detached from the global.
    /// </summary>
    private static async Task<AdminLinkResponse> UpdateLimitsAsync(
        Guid id,
        UpdateLimitsRequest request,
        KairosDbContext context,
        CancellationToken cancellationToken)
    {
        var link = await context.Links
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            ?? throw DomainException.NotFound("Link");

        link.MarginMinutes = request.MarginMinutes;
        link.MinNoticeMinutes = request.MinNoticeMinutes;
        link.DailyCap = request.DailyCap;
        link.StartIncrementMinutes = request.StartIncrementMinutes;

        await context.SaveChangesAsync(cancellationToken);

        return await ReadLinkAsync(context, id, cancellationToken);
    }

    /// <summary>
    /// Off, not gone. An archived link answers 404 to the public exactly as an unknown slug does,
    /// while the bookings already made through it stay resolvable. Idempotent, so a second press
    /// on a stale page is not an error.
    /// </summary>
    private static Task<AdminLinkResponse> ArchiveAsync(
        Guid id,
        KairosDbContext context,
        CancellationToken cancellationToken) =>
        SetActiveAsync(context, id, active: false, cancellationToken);

    private static Task<AdminLinkResponse> RestoreAsync(
        Guid id,
        KairosDbContext context,
        CancellationToken cancellationToken) =>
        SetActiveAsync(context, id, active: true, cancellationToken);

    private static async Task<AdminLinkResponse> SetActiveAsync(
        KairosDbContext context,
        Guid id,
        bool active,
        CancellationToken cancellationToken)
    {
        var link = await context.Links
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            ?? throw DomainException.NotFound("Link");

        link.Active = active;
        await context.SaveChangesAsync(cancellationToken);

        return await ReadLinkAsync(context, id, cancellationToken);
    }

    /// <summary>
    /// A link must offer at least one duration, and this is the only place that rule can live:
    /// it spans two tables, so it is not a SQLite CHECK, and SQLite has no deferred CHECK to
    /// postpone one to. Broken, the public booking page dies inside <c>Durations.Default</c> on
    /// an index that names nothing.
    /// </summary>
    private static void RequireDurations(IReadOnlyList<int> durations)
    {
        if (durations.Count == 0)
        {
            throw DomainException.Invalid(
                DurationsInvalidCode, "A link must offer at least one duration.");
        }

        if (durations.Distinct().Count() != durations.Count)
        {
            // The key of LinkDuration is the pair, so a repeat is not a second row — it is the
            // same row saved again, and EF would answer it with a tracking exception nobody can
            // read. Refused by name instead.
            throw DomainException.Invalid(
                DurationsInvalidCode, "A link offers each duration once.");
        }

        if (durations.Any(minutes =>
            minutes < SaveLinkRequest.MinDurationMinutes || minutes > SaveLinkRequest.MaxDurationMinutes))
        {
            throw DomainException.Invalid(
                DurationsInvalidCode,
                $"A duration is a whole number of minutes between {SaveLinkRequest.MinDurationMinutes} "
                + $"and {SaveLinkRequest.MaxDurationMinutes}.");
        }
    }

    private static void ReplaceDurations(Link link, IReadOnlyList<int> minutes)
    {
        link.Durations.Clear();

        for (var index = 0; index < minutes.Count; index++)
        {
            link.Durations.Add(new LinkDuration { Minutes = minutes[index], SortOrder = index });
        }
    }

    private static async Task RequirePatternAsync(
        KairosDbContext context,
        Guid patternId,
        CancellationToken cancellationToken)
    {
        var exists = await context.Patterns.AsNoTracking()
            .AnyAsync(pattern => pattern.Id == patternId, cancellationToken);

        if (!exists)
        {
            throw DomainException.Invalid(
                PatternUnknownCode, "That availability pattern does not exist.");
        }
    }

    /// <summary>
    /// <paramref name="exceptId"/> is the link being edited, so saving a link under the slug it
    /// already has is not a collision with itself.
    /// </summary>
    private static async Task RequireSlugFreeAsync(
        KairosDbContext context,
        string slug,
        Guid? exceptId,
        CancellationToken cancellationToken)
    {
        var taken = await context.Links.AsNoTracking()
            .AnyAsync(link => link.Slug == slug && link.Id != exceptId, cancellationToken);

        if (taken)
        {
            throw DomainException.Conflict(
                SlugTakenCode, "Another link already answers to that slug.");
        }
    }

    /// <summary>The link a write just touched, read back so the caller never has to guess.</summary>
    private static async Task<AdminLinkResponse> ReadLinkAsync(
        KairosDbContext context,
        Guid id,
        CancellationToken cancellationToken) =>
        (await ReadLinksAsync(context, id, cancellationToken)).Single();

    /// <summary>
    /// Every link, or one of them. Projected in two steps rather than one, the same way the public
    /// list does it: the durations subquery translates against an anonymous shape, and a record
    /// constructor taking an <c>IReadOnlyList</c> is not something to make the provider argue with.
    /// </summary>
    private static async Task<List<AdminLinkResponse>> ReadLinksAsync(
        KairosDbContext context,
        Guid? id,
        CancellationToken cancellationToken)
    {
        var rows = await context.Links.AsNoTracking()
            .Where(link => id == null || link.Id == id)
            .OrderBy(link => link.Name)
            .Select(link => new
            {
                link.Id,
                link.Slug,
                link.Name,
                link.Description,
                link.PatternId,
                PatternName = link.Pattern.Name,
                link.Active,
                Durations = link.Durations.OrderBy(duration => duration.SortOrder)
                    .Select(duration => duration.Minutes)
                    .ToList(),
                link.MarginMinutes,
                link.MinNoticeMinutes,
                link.DailyCap,
                link.StartIncrementMinutes,
            })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => new AdminLinkResponse(
            row.Id,
            row.Slug,
            row.Name,
            row.Description,
            row.PatternId,
            row.PatternName,
            row.Active,
            row.Durations,
            row.MarginMinutes,
            row.MinNoticeMinutes,
            row.DailyCap,
            row.StartIncrementMinutes))];
    }
}
