using Kairos.Api.Shared;

namespace Kairos.Api.Features.Admin;

/// <summary>
/// Where to send an operator who is not signed in. One string, and deliberately nothing else.
/// </summary>
/// <remarks>
/// This is read by a browser that has just been refused, so the response has to be safe to hand
/// to anybody at all. Adding the provider's name, the issuer URL, the client id or whether a
/// session ever existed would each answer a question an anonymous caller has no business asking,
/// and none of them is needed to draw a link.
/// </remarks>
public sealed record SignInResponse(string SignInUrl);

/// <summary>
/// The one anonymous route the locked admin screen reads, and the configuration behind it.
/// </summary>
public static class SignInEndpoints
{
    /// <summary>
    /// This application's own challenge route, which is already provider-agnostic: the OIDC
    /// handler discovers every endpoint from <c>Authentication:Authority</c>, so Pocket ID,
    /// Authentik, Keycloak and anything else that speaks OIDC all arrive here unchanged.
    /// </summary>
    public const string DefaultUrl = "/admin/login";

    /// <summary>
    /// <c>Authentication__SignInUrl</c> in the environment. Set it only for a deployment that
    /// wants the operator sent somewhere else first — a portal, a VPN gate, a provider's own
    /// account page — before the challenge above.
    /// </summary>
    public const string ConfigurationKey = "Authentication:SignInUrl";

    /// <summary>
    /// Anonymous by necessity rather than by choice, like the two routes named in
    /// <see cref="AdminGroup"/>: a signed-out operator is the only reader this has, and behind
    /// the gate it would answer 401 to exactly the person it exists for.
    /// </summary>
    public static RouteGroupBuilder MapSignIn(this RouteGroupBuilder group, string signInUrl)
    {
        group.MapGet("/sign-in", () => new SignInResponse(signInUrl));
        return group;
    }
}
