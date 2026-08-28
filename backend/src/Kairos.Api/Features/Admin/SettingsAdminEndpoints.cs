using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Shared;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using NodaTime;
using NodaTime.Text;

namespace Kairos.Api.Features.Admin;

/// <summary>
/// How many links have written their own value for each of the four, and therefore how many will
/// ignore a change to the global beside it. Counted rather than named: the question asked while
/// changing a global is "will this reach anything?", and the links screen one door away already
/// says which limits are its own.
/// </summary>
public sealed record OverrideCountsResponse(
    int MarginMinutes,
    int MinNoticeMinutes,
    int DailyCap,
    int StartIncrementMinutes);

/// <summary>
/// The settings screen's read, and the organizer's zone for every screen that names it.
/// </summary>
/// <remarks>
/// <para>
/// The four limits are plain <c>int</c> and never nullable: this is the level a link's null
/// inherits from, and nullable here would imply a level above the global that does not exist.
/// </para>
/// <para>
/// <see cref="SignedInAs"/> is the operator's name as the identity provider gave it, null when it
/// gave none. It rides on this response because the workbench asks both questions when it opens,
/// and a second route for one string would have no other reader.
/// </para>
/// </remarks>
public sealed record SettingsResponse(
    string TimeZone,
    string? SignedInAs,
    int MarginMinutes,
    int MinNoticeMinutes,
    int DailyCap,
    int StartIncrementMinutes,
    int ProposalHoldHours,
    OverrideCountsResponse Overrides,
    string? PublicBaseUrl);

/// <summary>
/// One zone as the picker offers it. The offset is the zone's offset at this instant, a label
/// rather than a fact about the zone — <c>Europe/Warsaw</c> is <c>+01:00</c> in January and
/// <c>+02:00</c> in July. It is here because the id means nothing to somebody scanning for
/// <c>+02:00</c>, and nothing reads it back.
/// </summary>
public sealed record TimeZoneOptionResponse(string Id, string Offset);

/// <summary>The six values the operator sets, all of them, every time.</summary>
/// <remarks>
/// The four limits' floors are a link's overrides' floors, for the same reasons: a negative margin
/// inverts the padded busy interval and starts creating overlaps that look like availability, and
/// a zero start increment makes the generator's minute step never advance.
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

    [Range(MinIncrementMinutes, int.MaxValue)]
    public required int StartIncrementMinutes { get; init; }

    /// <summary>
    /// How long a proposed move holds its slot. Floored above zero because a zero-hour hold has
    /// lapsed before the invitee's mail is composed, leaving the mechanism holding nothing.
    /// </summary>
    [Range(MinProposalHoldHours, int.MaxValue)]
    public required int ProposalHoldHours { get; init; }

    private const int MinLimit = 0;
    private const int MinIncrementMinutes = 1;
    private const int MinProposalHoldHours = 1;
    private const int MaxTimeZoneIdLength = 100;
}

/// <summary>
/// <para>
/// <b>Changing the zone rewrites nothing.</b> Every booking's <c>StartUtc</c> is an instant and
/// stays where it is; what moves is the wall clock the availability rules describe, so a pattern
/// reading 09:00–17:00 shifts tomorrow's offered window while the meetings already made do not.
/// </para>
/// <para>
/// <b>No provider list to maintain.</b> The picker's options are
/// <see cref="DateTimeZoneProviders.Tzdb"/>, which NodaTime already carries: nothing hand-kept to
/// go stale, no ICU dependency, and the same database the scheduler resolves against — which is why
/// the check on the way in reads from it rather than from the operating system.
/// </para>
/// </summary>
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
        IOptions<AppOptions> app,
        CancellationToken cancellationToken) =>
        DescribeAsync(context, user, app, cancellationToken);

    private static async Task<SettingsResponse> SaveAsync(
        SaveSettingsRequest request,
        ClaimsPrincipal user,
        KairosDbContext context,
        IOptions<AppOptions> app,
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
        settings.ProposalHoldHours = request.ProposalHoldHours;

        await context.SaveChangesAsync(cancellationToken);

        return await DescribeAsync(context, user, app, cancellationToken);
    }

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
        IOptions<AppOptions> app,
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
            settings.ProposalHoldHours,
            await CountOverridesAsync(context, cancellationToken),
            app.Value.PublicBaseUrl);
    }

    /// <summary>
    /// <see cref="ClaimsIdentity.Name"/> is the id token's <c>name</c> claim, which the OIDC
    /// handler asks for with the <c>profile</c> scope it requests by default. A provider sending no
    /// name leaves this null and the workbench draws no identity block, rather than presenting a
    /// subject identifier or an email address as a person's name.
    /// </summary>
    private static string? SignedInAs(ClaimsPrincipal user)
    {
        var name = user.Identity?.Name;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// Counted in memory rather than as four SQL aggregates: this is a single-operator scheduler
    /// whose links table is read whole on the screen next door, so the row count cannot make that
    /// query shape matter.
    /// </summary>
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
