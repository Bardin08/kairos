using System.Security.Claims;
using System.Text.Encodings.Web;

using Kairos.Api.Persistence;
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
    /// <summary>The name the substituted provider answers to. Nothing else authenticates.</summary>
    public const string Scheme = "TestOperator";

    /// <summary>
    /// The subject the substituted provider issues, spelled the way a real one does. Public
    /// because an allowlist is configuration naming this same value, so a test that writes one
    /// has to be able to say it.
    /// </summary>
    public const string Subject = "kairos-test-operator";

    /// <summary>
    /// A client on its own host, so <paramref name="substitutions"/> can replace a service the
    /// composition root registered. Taken on the factory rather than on <see cref="KairosApp"/>
    /// so that <c>WithWebHostBuilder(…).CreateOperatorClient()</c> composes — a test that varies
    /// configuration needs no parameter here to do it. The database is the factory's, so anything
    /// seeded through <see cref="KairosApp.Seed"/> is visible here.
    /// </summary>
    /// <param name="group">
    /// The group the session carries. The default is the one the application admits; null is a
    /// principal the identity provider authenticated and Kairos does not trust, and any other
    /// value is one it authenticated for a different application. Both are cases
    /// <see cref="AdminBoundaryTests"/> pins.
    /// </param>
    /// <param name="subject">
    /// The subject the session carries, issued by default because a real provider always sends
    /// one. It only decides anything where a deployment has written an allowlist.
    /// </param>
    /// <param name="subjectClaimType">
    /// How that subject is spelled. The default is what a principal really carries: the OIDC
    /// handler maps inbound claims unless told not to, so <c>sub</c> arrives rewritten to the
    /// ClaimTypes URI. A provider with mapping turned off is the other spelling, and both are
    /// live arms of <see cref="AdminAuthorization.IsOperator"/>.
    /// </param>
    public static HttpClient CreateOperatorClient(
        this WebApplicationFactory<KairosDbContext> app,
        Action<IServiceCollection>? substitutions = null,
        string? group = AdminAuthorization.DefaultGroup,
        string? subject = Subject,
        string subjectClaimType = ClaimTypes.NameIdentifier) =>
        app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication()
                .AddScheme<SignedInOptions, SignedInHandler>(Scheme, options =>
                {
                    options.Group = group;
                    options.Subject = subject;
                    options.SubjectClaimType = subjectClaimType;
                });

            // Only authenticating is substituted, and deliberately not by AddAuthentication(Scheme):
            // that sets the default for challenging, forbidding and signing in as well, and the
            // body a refused operator is handed is written by the application's own cookie
            // handler. Substituting the identity provider must not substitute the refusal.
            services.Configure<AuthenticationOptions>(
                options => options.DefaultAuthenticateScheme = Scheme);

            substitutions?.Invoke(services);
        })).CreateClient();

    /// <summary>What the substituted provider puts in the ticket, and the only thing it varies.</summary>
    private sealed class SignedInOptions : AuthenticationSchemeOptions
    {
        public string? Group { get; set; }

        public string? Subject { get; set; }

        public string SubjectClaimType { get; set; } = ClaimTypes.NameIdentifier;
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

            if (Options.Subject is { } subject)
            {
                claims.Add(new Claim(Options.SubjectClaimType, subject));
            }

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
