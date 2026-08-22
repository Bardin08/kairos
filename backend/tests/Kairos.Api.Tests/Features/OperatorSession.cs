using System.Security.Claims;
using System.Text.Encodings.Web;

using Kairos.Api.Security;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// A client that arrives with an operator's session, for the tests that are about what an admin
/// route ANSWERS rather than about who may reach it.
/// </summary>
/// <remarks>
/// The gate itself is never substituted — <see cref="AdminBoundaryTests"/> exercises the real one
/// and would catch a route that fell outside it. What is replaced is only the identity provider,
/// because the alternative is a test that talks OpenID Connect to a Pocket ID that does not
/// exist. Everything else is the application as it ships: same routes, same authorization policy,
/// same handlers.
/// </remarks>
public static class OperatorSession
{
    /// <summary>Registered last, so it becomes the default scheme and the policy authenticates against it.</summary>
    public const string Scheme = "TestOperator";

    /// <summary>
    /// A client on its own host, so <paramref name="substitutions"/> can replace a service the
    /// composition root registered. The database is the factory's, so anything seeded through
    /// <see cref="KairosApp.Seed"/> is visible here.
    /// </summary>
    /// <param name="group">
    /// The group the session carries. The default is the one the application admits; null is a
    /// principal the identity provider authenticated and Kairos does not trust, which is the case
    /// <see cref="AdminBoundaryTests"/> pins.
    /// </param>
    public static HttpClient CreateOperatorClient(
        this KairosApp app,
        string? group = AdminAuthorization.DefaultGroup,
        Action<IServiceCollection>? substitutions = null) =>
        app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(Scheme)
                .AddScheme<SignedInOptions, SignedInHandler>(
                    Scheme, options => options.Group = group);
            substitutions?.Invoke(services);
        })).CreateClient();

    /// <summary>What the substituted provider puts in the ticket, and the only thing it varies.</summary>
    public sealed class SignedInOptions : AuthenticationSchemeOptions
    {
        public string? Group { get; set; }
    }

    private sealed class SignedInHandler(
        IOptionsMonitor<SignedInOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<SignedInOptions>(options, logger, encoder)
    {
        private const string Operator = "operator";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            List<Claim> claims = [new Claim(ClaimTypes.Name, Operator)];
            if (Options.Group is { } group)
            {
                claims.Add(new Claim(AdminAuthorization.GroupClaimType, group));
            }

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
