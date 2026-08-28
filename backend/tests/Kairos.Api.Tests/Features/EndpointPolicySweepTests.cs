using Kairos.Api.Features.Admin;
using Kairos.Api.Security;
using Kairos.Api.Shared;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// Two sweeps over the routes the running application actually registers, rather than over a list
/// of the ones somebody remembered to write down. <see cref="AdminBoundaryTests"/> proves the admin
/// GROUP carries the policy, at the place the group is defined; nothing proves that a given route
/// was mapped onto that group rather than onto <see cref="ApiVersions.V1"/> beside it. That gap is
/// silent in the only direction that matters — an over-gated public route breaks booking loudly,
/// while an under-gated operator route breaks nothing and protects nothing.
/// </summary>
/// <remarks>
/// The two allowlists are the half that earns its keep. A sweep with no list catches only the
/// mistake nobody makes; with one, a route that is public or unmetered cannot ship without an edit
/// here for somebody to read. They turn the reasons <see cref="AdminGroup"/>'s remarks give in
/// prose into something that can fail.
/// </remarks>
public class EndpointPolicySweepTests
{
    private const string AdminPrefix = $"{ApiVersions.V1}/admin";

    /// <summary>Named so an endpoint carrying no pattern reads as a finding rather than as a blank.</summary>
    private const string UnnamedEndpoint = "<an endpoint with no route pattern>";

    /// <summary>
    /// The single-page fallback, spelled as <c>MapFallbackToFile</c> registers it: no leading
    /// slash, unlike every other pattern in this file.
    /// </summary>
    private const string SinglePageFallback = "{*path:nonfile}";

    /// <summary>
    /// Everything the API prefix does not otherwise claim, answering 404 so that
    /// <see cref="SinglePageFallback"/> cannot serve the page for a mistyped API path.
    /// </summary>
    private const string UnmappedApiPath = "/api/{**rest}";

    /// <summary>
    /// The sign-out's path. Its sibling is <see cref="SignInEndpoints.DefaultUrl"/>; this one has
    /// no constant in the application, being mapped inline in <c>Program.cs</c>.
    /// </summary>
    private const string SignOutPath = "/admin/logout";

    /// <summary>
    /// Every route this application answers outside the admin prefix, each anonymous on purpose.
    /// Anything reaching this list that an operator would call is a defect, not an entry.
    /// </summary>
    /// <remarks>
    /// An entry approves a PATTERN, so a second verb mapped on an approved pattern inherits that
    /// approval — a public GET and an ungated operator POST on one path would pass both sweeps.
    /// Nothing here maps such a pair, and keying on verb-plus-pattern would roughly double these
    /// lists to catch a shape the codebase does not have. An unmetered second verb is still caught:
    /// the rate-limit read runs per endpoint, before patterns are deduplicated.
    /// </remarks>
    private static readonly string[] PublicOnPurpose =
    [
        // The challenge: a browser navigation to another origin, which nothing behind the gate
        // could start. Issues only a redirect.
        SignInEndpoints.DefaultUrl,

        // The sign-out, a redirect for the same reason.
        SignOutPath,

        // Liveness and readiness. Anonymous because a kubelet holds no session, and outside the
        // version prefix because their paths live in a manifest a version bump must not move.
        HealthEndpoints.LivenessPath,
        HealthEndpoints.ReadinessPath,

        // Where to sign in, for a browser that has just been refused. Behind the gate it would 401
        // at the one reader it exists for.
        ApiVersions.V1 + "/sign-in",

        // The OAuth return leg. Its path is registered byte-for-byte with the provider, and the
        // browser arriving on it holds that provider's redirect rather than a session of ours;
        // signed state is what guards it.
        ApiVersions.V1 + "/integrations/{provider}/callback",

        // The booking page's own reads: the link an invitee opened, and the times it offers.
        ApiVersions.V1 + "/links/{slug}",
        ApiVersions.V1 + "/links/{slug}/slots",

        // Booking, and then every route keyed by the booking token that answer hands back. The
        // token is the credential — an invitee never holds a session.
        ApiVersions.V1 + "/bookings",
        ApiVersions.V1 + "/bookings/{token}",
        ApiVersions.V1 + "/bookings/{token}/cancel",
        ApiVersions.V1 + "/bookings/{token}/reschedule",

        // The invitee's side of an operator's proposal, keyed by a token the same way.
        ApiVersions.V1 + "/bookings/proposals/{token}",
        ApiVersions.V1 + "/bookings/proposals/{token}/accept",
        ApiVersions.V1 + "/bookings/proposals/{token}/decline",

        // The personal invite behind a link somebody was sent. Same shape and same reason: whoever
        // holds the URL gets the prefill.
        ApiVersions.V1 + "/invites/{token}",

        // Neither is a feature. One refuses an unmapped API path, the other serves the page React
        // routes from; a literal segment beats a wildcard, so neither shadows a route above it.
        UnmappedApiPath,
        SinglePageFallback,
    ];

    /// <summary>
    /// The routes outside the admin prefix that deliberately carry no budget. A limited route
    /// declares its class at its own mapping site and never appears here, so this list does not
    /// weaken <see cref="RateLimitPolicies.RateLimited"/>'s promise that an endpoint added later is
    /// limited beside itself rather than by an edit somewhere else.
    /// </summary>
    /// <remarks>
    /// Nothing behind the admin policy is limited either — <see cref="AdminGroup"/> argues that,
    /// and this sweep deliberately asks nothing about it.
    /// </remarks>
    private static readonly string[] UnlimitedOnPurpose =
    [
        // Redirects, both of them, running no check and touching no database.
        SignInEndpoints.DefaultUrl,
        SignOutPath,

        // Liveness runs no check at all, and refusing it restarts a working pod. Readiness runs
        // one existence query against the local database, and refusing that pulls the pod out of
        // rotation instead. Both are polled on a fixed interval.
        HealthEndpoints.LivenessPath,
        HealthEndpoints.ReadinessPath,

        // One configured string, no database.
        ApiVersions.V1 + "/sign-in",

        // Without a valid signed state it is an HMAC check and a log line, never an outbound
        // request.
        ApiVersions.V1 + "/integrations/{provider}/callback",

        // A constant 404, which computes nothing, and the page React routes from. UseStaticFiles
        // runs ahead of routing, so an asset on disk never reaches the second at all: a budget
        // there would meter navigations into the app.
        UnmappedApiPath,
        SinglePageFallback,
    ];

    /// <summary>
    /// The assertion <see cref="AdminBoundaryTests"/> cannot make: not that the group is gated, but
    /// that every route living under its prefix was mapped through it.
    /// </summary>
    [Fact]
    public void EveryRouteUnderTheAdminPrefix_WhicheverGroupItWasMappedOn_CarriesTheAdminPolicy()
    {
        using var app = new KairosApp();

        var ungated = Patterns(Endpoints(app)
            .Where(IsUnderTheAdminPrefix)
            .Where(endpoint => !CarriesTheAdminPolicy(endpoint)));

        ungated.ShouldBeEmpty(
            $"These routes sit under {AdminPrefix} without {AdminGroup.Policy}, so they were "
            + "mapped somewhere other than through MapAdmin().");
    }

    /// <summary>
    /// The other side of the same line, and the one a new route trips: anything not behind the
    /// policy is reachable by anybody, so it is named here or it is a defect.
    /// </summary>
    [Fact]
    public void EveryRouteOutsideTheAdminPrefix_IsNamedPublicOnPurpose_AndTheListNamesNothingElse()
    {
        using var app = new KairosApp();

        var outside = Patterns(Endpoints(app).Where(endpoint => !IsUnderTheAdminPrefix(endpoint)));

        outside.Except(PublicOnPurpose, StringComparer.Ordinal).ShouldBeEmpty(
            "These routes or endpoints are reachable without a session and nothing says they are "
            + "meant to be. "
            + $"An operator route belongs on the {nameof(AdminGroup.MapAdmin)} chain; only a route "
            + $"an anonymous caller must reach belongs in {nameof(PublicOnPurpose)}.");

        // The stale half. A pattern left behind after its route moved or died would silently
        // pre-approve whatever is mapped there next.
        PublicOnPurpose.Except(outside, StringComparer.Ordinal).ShouldBeEmpty(
            $"{nameof(PublicOnPurpose)} names routes this application no longer maps.");
    }

    /// <summary>
    /// Rule 20, read the way the limiter reads it. Deleting a <c>RateLimited</c> call was measured
    /// to leave the whole suite green before this existed.
    /// </summary>
    [Fact]
    public void EveryRouteOutsideTheAdminPrefix_CarriesARateLimitClass_UnlessItIsUnlimitedOnPurpose()
    {
        using var app = new KairosApp();

        var unmetered = Patterns(Endpoints(app)
            .Where(endpoint => !IsUnderTheAdminPrefix(endpoint))
            .Where(endpoint => endpoint.Metadata.GetMetadata<RateLimitClass>() is null));

        unmetered.Except(UnlimitedOnPurpose, StringComparer.Ordinal).ShouldBeEmpty(
            "These anonymous routes spend from no budget. Declare one beside the mapping with "
            + $"{nameof(RateLimitPolicies.RateLimited)}, or say here why this one has none.");

        UnlimitedOnPurpose.Except(unmetered, StringComparer.Ordinal).ShouldBeEmpty(
            $"{nameof(UnlimitedOnPurpose)} names routes that are no longer mapped, or that now "
            + "declare a budget of their own.");
    }

    /// <summary>
    /// The routes of the application as it ships, read while its host is alive. Resolved from the
    /// container rather than from the builder, so it is the composed set the server routes on.
    /// </summary>
    private static IEnumerable<Endpoint> Endpoints(KairosApp app) =>
        app.Services.GetRequiredService<EndpointDataSource>().Endpoints;

    /// <summary>
    /// A route pattern, or the best name an endpoint without one has. Deliberately not filtered to
    /// <see cref="RouteEndpoint"/>: a filter would drop such an endpoint from both sweeps in
    /// silence, where a name it carries surfaces as an unlisted route instead.
    /// </summary>
    private static string Pattern(Endpoint endpoint) =>
        (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? endpoint.DisplayName ?? UnnamedEndpoint;

    /// <summary>
    /// Distinct, because one pattern mapped for two verbs is one access decision, and ordered so a
    /// failure reads the same way twice.
    /// </summary>
    private static IReadOnlyList<string> Patterns(IEnumerable<Endpoint> endpoints) =>
        endpoints.Select(Pattern)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static bool IsUnderTheAdminPrefix(Endpoint endpoint) =>
        Pattern(endpoint).StartsWith(AdminPrefix, StringComparison.Ordinal);

    /// <summary>Read off the endpoint's own metadata, and never off a list of route names.</summary>
    private static bool CarriesTheAdminPolicy(Endpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(data => data.Policy == AdminGroup.Policy);
}
