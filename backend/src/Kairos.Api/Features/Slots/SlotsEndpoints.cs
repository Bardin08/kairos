using System.ComponentModel.DataAnnotations;

using Kairos.Api.Availability;
using Kairos.Api.Security;
using Kairos.Api.Shared;

using Microsoft.AspNetCore.Mvc;

using NodaTime;
using NodaTime.Text;

namespace Kairos.Api.Features.Slots;

/// <summary>
/// The rule the whole feature folder follows: an input gets a record and an attribute where the
/// attribute decides something nothing downstream would — a query value's presence, a numeric
/// bound, an address shape. A lone route segment going straight into a resolver gets neither, which
/// is why <c>GET /links/{slug}</c> and the three <c>/bookings/{token}</c> routes take a bare
/// parameter: a segment cannot be absent, and "no such thing" is the resolver's 404.
/// </summary>
/// <remarks>
/// The slug carries no pattern attribute for that reason. It had one, and an unusable slug then
/// answered 400 here and 404 on <c>GET /links/{slug}</c> — same input, two answers. The gate
/// protected nothing either: the slug's only use is one lookup on a unique index, which resolves
/// or throws before any window is expanded.
/// </remarks>
public sealed record GetSlotsRequest
{
    [FromRoute(Name = "slug")]
    [Required]
    public required string Slug { get; init; }

    [FromQuery(Name = "from")]
    [Required]
    public required string From { get; init; }

    [FromQuery(Name = "to")]
    [Required]
    public required string To { get; init; }

    [FromQuery(Name = "duration")]
    [Range(MinDurationMinutes, MaxDurationMinutes)]
    public int? Duration { get; init; }

    private const int MinDurationMinutes = 1;
    private const int MaxDurationMinutes = 1440;
}

/// <summary>
/// The public booking page's only read. The handler has no failure branch: the attributes above
/// refuse malformed shapes, the two date parses throw, and everything else is thrown inside
/// <see cref="SlotQuery"/> and turned into ProblemDetails by <see cref="DomainExceptionHandler"/>.
/// </summary>
public static class SlotsEndpoints
{
    /// <summary>
    /// <c>from</c> or <c>to</c> is not an ISO calendar date. Thrown rather than declared as a
    /// pattern attribute because no regular expression rejects 2026-02-30: the parse has to run
    /// either way, so a regex would only add a second, differently shaped 400 to the same route.
    /// </summary>
    public const string DateInvalidCode = "date_invalid";

    /// <summary>
    /// <c>to</c> is before <c>from</c>. Distinct from an empty result on purpose:
    /// <see cref="SlotQuery"/> answers an inverted range with an empty list, which the booking
    /// page would otherwise render as "no times available" for what is a malformed request.
    /// </summary>
    public const string RangeInvertedCode = "range_inverted";

    private const string FromParameter = "from";
    private const string ToParameter = "to";

    public static RouteGroupBuilder MapSlots(this RouteGroupBuilder group)
    {
        // A month of intervals materialized per call, from an anonymous caller, with no write to
        // make the cost visible afterwards — the cheapest denial-of-service target on this API.
        group.MapGet("/links/{slug}/slots", GetSlotsAsync).WithName("GetSlots")
            .RateLimited(RateLimitClass.AnonymousRead);
        return group;
    }

    private static async Task<IReadOnlyList<string>> GetSlotsAsync(
        [AsParameters] GetSlotsRequest request,
        SlotQuery slots,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var from = ParseDate(request.From, FromParameter);
        var to = ParseDate(request.To, ToParameter);
        if (to < from)
        {
            throw DomainException.Invalid(
                RangeInvertedCode, $"'{ToParameter}' is before '{FromParameter}'.");
        }

        // One clock read, passed down. Minimum notice is measured from it, and the create-booking
        // path re-checks against the same reading, so a slot offered at the notice boundary is
        // still there when it is submitted.
        var results = await slots.GetSlotsAsync(
            request.Slug, request.Duration, from, to, clock.GetCurrentInstant(), cancellationToken);

        return [.. results.Starts.Select(InstantPattern.ExtendedIso.Format)];
    }

    /// <summary>
    /// The caller's value is never echoed back. A ProblemDetails title lands in whatever the
    /// client does with it, and reflecting arbitrary input is a habit worth not having.
    /// </summary>
    private static LocalDate ParseDate(string value, string parameter)
    {
        var parsed = LocalDatePattern.Iso.Parse(value);
        if (!parsed.Success)
        {
            throw DomainException.Invalid(
                DateInvalidCode, $"'{parameter}' must be an ISO calendar date, such as 2026-09-07.");
        }

        return parsed.Value;
    }
}
