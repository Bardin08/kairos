using Kairos.Api.Shared;

namespace Kairos.Api.Features.Admin;

/// <summary>
/// Carries the sign-in URL and nothing else, because an anonymous caller reads it. The provider
/// name, issuer URL, client id and whether a session ever existed are all omitted deliberately:
/// none is needed to draw a link, and each answers a question the reader has no business asking.
/// </summary>
public sealed record SignInResponse(string SignInUrl);

public static class SignInEndpoints
{
    /// <summary>
    /// Provider-agnostic already: the OIDC handler discovers every endpoint from
    /// <c>Authentication:Authority</c>, so any compliant provider arrives here unchanged.
    /// </summary>
    public const string DefaultUrl = "/admin/login";

    /// <summary>
    /// Set only by a deployment that sends the operator somewhere else first — a portal, a VPN
    /// gate, a provider's account page — before the challenge above.
    /// </summary>
    public const string ConfigurationKey = "Authentication:SignInUrl";

    /// <summary>
    /// Anonymous of necessity, like the other routes <see cref="AdminGroup"/> keeps outside the
    /// prefix: a signed-out operator is its only reader, so behind the gate it would answer 401
    /// to exactly the person it exists for.
    /// </summary>
    public static RouteGroupBuilder MapSignIn(this RouteGroupBuilder group, string signInUrl)
    {
        group.MapGet("/sign-in", () => new SignInResponse(signInUrl));
        return group;
    }
}
