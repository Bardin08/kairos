using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Shared;

using Microsoft.EntityFrameworkCore;

using NodaTime;
using NodaTime.Text;

namespace Kairos.Api.Features.Admin;

/// <summary>
/// How many links have written their own value for each of the four, and therefore how many will
/// ignore a change to the global beside it.
/// </summary>
/// <remarks>
/// Counted rather than named. The links themselves are one screen away and already say which of
/// their limits are their own; what this answers is the question asked while changing a global —
/// "will this actually reach anything?" — and a count answers it in a word.
/// </remarks>
public sealed record OverrideCountsResponse(
    int MarginMinutes,
    int MinNoticeMinutes,
    int DailyCap,
    int StartIncrementMinutes);

/// <summary>
/// The whole of the settings screen's read, and the organizer's zone for every screen that needs
/// to name it.
/// </summary>
/// <remarks>
/// <para>
/// The four limits are the globals themselves, so they are plain <c>int</c> and never nullable.
/// This is the level a link's null inherits from; a nullable field here would imply a level above
/// the global that does not exist.
/// </para>
/// <para>
/// <see cref="SignedInAs"/> is the operator's name as the identity provider gave it, and null when
/// it gave none — Kairos never invents one. It rides on this response rather than on a route of
/// its own because the workbench asks both questions in the same breath when it opens, and a
/// second route to answer one string is a route with no other reader.
/// </para>
/// </remarks>
public sealed record SettingsResponse(
    string TimeZone,
    string? SignedInAs,
    int MarginMinutes,
    int MinNoticeMinutes,
    int DailyCap,
    int StartIncrementMinutes,
    OverrideCountsResponse Overrides);

/// <summary>
/// One zone as the picker offers it: the IANA id, and what it is reading right now.
/// </summary>
/// <remarks>
/// The offset is the zone's offset at this instant and is therefore a label rather than a fact
/// about the zone — <c>Europe/Warsaw</c> is <c>+01:00</c> in January and <c>+02:00</c> in July. It
/// is here because <c>Europe/Warsaw</c> means nothing to somebody scanning for <c>+02:00</c>, and
/// nothing reads it back.
/// </remarks>
public sealed record TimeZoneOptionResponse(string Id, string Offset);

/// <summary>
/// The five values the operator sets, all of them, every time.
/// </summary>
/// <remarks>
/// The floors are the same ones a link's overrides answer to, and for the same reasons: a negative
/// margin inverts the padded busy interval and starts creating overlaps that look like
/// availability, and a zero start increment makes the generator's minute step never advance.
/// </remarks>
public sealed record SaveSettingsRequest
{
    /// <summary>An IANA id from the TZDB this build carries. Checked against it, not parsed loosely.</summary>
    [Required]
    [StringLength(MaxTimeZoneIdLength)]
    public required string TimeZoneId { get; init; }

    [Range(MinLimit, int.MaxValue)]
    public required int MarginMinutes { get; init; }

    [Range(MinLimit, int.MaxValue)]
    public required int MinNoticeMinutes { get; init; }

    [Range(MinLimit, int.MaxValue)]
    public required int DailyCap { get; init; }

    /// <summary>The one of the four with a different floor. Zero never advances the slot grid.</summary>
    [Range(MinIncrementMinutes, int.MaxValue)]
    public required int StartIncrementMinutes { get; init; }

    private const int MinLimit = 0;
    private const int MinIncrementMinutes = 1;
    private const int MaxTimeZoneIdLength = 100;
}

/// <summary>
/// The organizer's zone and the four global limits every link's overrides resolve against.
/// </summary>
/// <remarks>
/// <para>
/// <b>Changing the zone rewrites nothing.</b> Every booking's <c>StartUtc</c> is an instant and
/// stays exactly where it is. What moves is the wall clock the availability rules describe: a
/// pattern that says 09:00–17:00 means nine to five in whichever zone this row names, so the
/// window offered tomorrow shifts while the meetings already made do not.
/// </para>
/// <para>
/// <b>No provider list to maintain.</b> The picker's options are
/// <see cref="DateTimeZoneProviders.Tzdb"/>, which NodaTime already carries — so there is no
/// hand-kept list to go stale and no ICU dependency to reintroduce. It is also the same database
/// the scheduler resolves against, which is why the check on the way in reads from it rather than
/// from anything the operating system happens to know.
/// </para>
/// </remarks>
public static class SettingsAdminEndpoints
{
    /// <summary>A zone id this build's TZDB does not carry. Refused here rather than at the first booking.</summary>
    public const string TimeZoneUnknownCode = "time_zone_unknown";

    /// <summary>
    /// <c>+02:00</c>, sign always shown. Not the general pattern, which drops a zero minute field
    /// and would print <c>+02</c> beside <c>+05:30</c> in the same list.
    /// </summary>
    private static readonly OffsetPattern OffsetLabel =
        OffsetPattern.CreateWithInvariantCulture("+HH:mm");

    public static RouteGroupBuilder MapSettingsAdmin(this RouteGroupBuilder group)
    {
        group.MapGet("/settings", ReadAsync).WithName("ReadSettings");
        group.MapPut("/settings", SaveAsync).WithName("SaveSettings");

        // Its own route rather than a field of the read above: the list is every zone there is,
        // and the two screens that need only the organizer's own would be downloading all of them
        // to read one string.
        group.MapGet("/settings/zones", ListTimeZones).WithName("ListTimeZones");

        return group;
    }

    private static Task<SettingsResponse> ReadAsync(
        ClaimsPrincipal user,
        KairosDbContext context,
        CancellationToken cancellationToken) =>
        DescribeAsync(context, user, cancellationToken);

    private static async Task<SettingsResponse> SaveAsync(
        SaveSettingsRequest request,
        ClaimsPrincipal user,
        KairosDbContext context,
        CancellationToken cancellationToken)
    {
        if (DateTimeZoneProviders.Tzdb.GetZoneOrNull(request.TimeZoneId) is null)
        {
            throw DomainException.Invalid(
                TimeZoneUnknownCode,
                $"'{request.TimeZoneId}' is not a zone this Kairos knows. Pick one from the list.");
        }

        // Tracked, because this is the one row in the table and the write is a plain update of it.
        var settings = await context.Settings
            .SingleAsync(row => row.Id == Settings.SingletonId, cancellationToken);

        settings.TimeZoneId = request.TimeZoneId;
        settings.MarginMinutes = request.MarginMinutes;
        settings.MinNoticeMinutes = request.MinNoticeMinutes;
        settings.DailyCap = request.DailyCap;
        settings.StartIncrementMinutes = request.StartIncrementMinutes;

        await context.SaveChangesAsync(cancellationToken);

        return await DescribeAsync(context, user, cancellationToken);
    }

    /// <summary>
    /// Every zone in the TZDB, by id, each labelled with what it is reading now.
    /// </summary>
    private static IReadOnlyList<TimeZoneOptionResponse> ListTimeZones(IClock clock)
    {
        var now = clock.GetCurrentInstant();
        var tzdb = DateTimeZoneProviders.Tzdb;

        // Ordered by id rather than by offset: an operator arrives knowing the name of the place
        // they live in, and the offset is what confirms the pick rather than what finds it.
        return [.. tzdb.Ids.Order(StringComparer.Ordinal).Select(id =>
            new TimeZoneOptionResponse(id, OffsetLabel.Format(tzdb[id].GetUtcOffset(now))))];
    }

    private static async Task<SettingsResponse> DescribeAsync(
        KairosDbContext context,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var settings = await context.Settings.AsNoTracking()
            .SingleAsync(row => row.Id == Settings.SingletonId, cancellationToken);

        return new SettingsResponse(
            settings.TimeZoneId,
            SignedInAs(user),
            settings.MarginMinutes,
            settings.MinNoticeMinutes,
            settings.DailyCap,
            settings.StartIncrementMinutes,
            await CountOverridesAsync(context, cancellationToken));
    }

    /// <summary>
    /// Whatever the identity provider called this operator, or null.
    /// </summary>
    /// <remarks>
    /// <see cref="ClaimsIdentity.Name"/> is the <c>name</c> claim of the id token, which the OIDC
    /// handler asks for with the <c>profile</c> scope it requests by default. A provider that
    /// sends no name leaves this null and the workbench simply draws no identity block — the
    /// alternative is a subject identifier or an email address presented as a person's name, and
    /// a made-up name is worse than none.
    /// </remarks>
    private static string? SignedInAs(ClaimsPrincipal user)
    {
        var name = user.Identity?.Name;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// The four counts, off one read of the four nullable columns.
    /// </summary>
    /// <remarks>
    /// Counted in memory rather than as four SQL aggregates. This is a single-operator scheduler
    /// whose links table is read whole on the screen next door, and four conditional counts pushed
    /// into one statement is a query shape worth arguing with the provider about only if the row
    /// count could ever make it matter.
    /// </remarks>
    private static async Task<OverrideCountsResponse> CountOverridesAsync(
        KairosDbContext context,
        CancellationToken cancellationToken)
    {
        var links = await context.Links.AsNoTracking()
            .Select(link => new
            {
                link.MarginMinutes,
                link.MinNoticeMinutes,
                link.DailyCap,
                link.StartIncrementMinutes,
            })
            .ToListAsync(cancellationToken);

        return new OverrideCountsResponse(
            links.Count(link => link.MarginMinutes is not null),
            links.Count(link => link.MinNoticeMinutes is not null),
            links.Count(link => link.DailyCap is not null),
            links.Count(link => link.StartIncrementMinutes is not null));
    }
}
