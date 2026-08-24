using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Security;
using Kairos.Api.Shared;

using Microsoft.EntityFrameworkCore;

namespace Kairos.Api.Features.Links;

/// <summary>
/// One bookable event type as the booking page needs it. The organizer's zone rides along
/// because every instant this API returns is derived from it, and a page that renders times
/// without saying whose day they belong to is a page that books the wrong hour.
/// </summary>
public sealed record LinkResponse(
    string Slug,
    string Name,
    string Description,
    IReadOnlyList<int> Durations,
    string TimeZone);

/// <summary>
/// The read side of links; the write is <see cref="Admin.LinksAdminEndpoints"/>. The rule that a
/// link may never be left with zero durations spans two tables and therefore cannot be a SQLite
/// CHECK, so <c>RequireDurations</c> enforces it there, on the create and update that can break
/// it. <see cref="Availability.SlotQuery"/> still refuses such a link with
/// <c>link_has_no_durations</c> rather than letting the public page fall over.
/// </summary>
public static class LinksEndpoints
{
    public static RouteGroupBuilder MapLinks(this RouteGroupBuilder group)
    {
        group.MapGet("/links/{slug}", GetAsync).WithName("GetLink")
            .RateLimited(RateLimitClass.AnonymousRead);
        return group;
    }

    private static async Task<LinkResponse> GetAsync(
        string slug,
        KairosDbContext context,
        CancellationToken cancellationToken)
    {
        var timeZone = await GetTimeZoneAsync(context, cancellationToken);
        var link = await Active(context)
            .Include(candidate => candidate.Durations)
            .FirstOrDefaultAsync(candidate => candidate.Slug == slug, cancellationToken)
            // Not "Link '{slug}'": the caller's value is never reflected into a response body,
            // here or in SlotsEndpoints. It costs the message nothing — the slug is in the URL
            // the caller just sent — and it keeps an arbitrary string out of whatever the client
            // does with a ProblemDetails title.
            ?? throw DomainException.NotFound("Link");

        var durations = link.Durations
            .OrderBy(duration => duration.SortOrder)
            .Select(duration => duration.Minutes)
            .ToList();

        return new LinkResponse(link.Slug, link.Name, link.Description, durations, timeZone);
    }

    /// <summary>
    /// Inactive links are invisible here and 404 on the way in, exactly as an unknown slug does.
    /// Distinguishing the two would tell an anonymous caller which slugs used to exist.
    /// </summary>
    private static IQueryable<Link> Active(KairosDbContext context) =>
        context.Links.AsNoTracking().Where(link => link.Active);

    private static Task<string> GetTimeZoneAsync(
        KairosDbContext context,
        CancellationToken cancellationToken) =>
        context.Settings.AsNoTracking()
            .Where(row => row.Id == Settings.SingletonId)
            .Select(row => row.TimeZoneId)
            .SingleAsync(cancellationToken);
}
