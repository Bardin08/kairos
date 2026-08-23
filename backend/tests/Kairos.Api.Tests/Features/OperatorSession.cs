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
/// A client arriving with an operator's session, for tests about what an admin route ANSWERS
/// rather than who may reach it.
/// </summary>
/// <remarks>
/// Only the identity provider is replaced, because the alternative is talking OpenID Connect to
/// a Pocket ID that does not exist. The gate is the real one — <see cref="AdminBoundaryTests"/>
/// exercises it — and so are the routes, the policy and the handlers.
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
    /// A client on its own host, so <paramref name="substitutions"/> can replace a registered
    /// service. Taken on the factory so <c>WithWebHostBuilder(…).CreateOperatorClient()</c>
    /// composes; the database is the factory's, so <see cref="KairosApp.Seed"/> is visible.
    /// </summary>
    /// <param name="groups">
    /// One claim each. Null is the group the application admits, empty a principal Kairos does
    /// not trust, anything else memberships held elsewhere; all three are pinned.
    /// </param>
    /// <param name="groupClaimType">Varied for the test pinning case-insensitive matching.</param>
    /// <param name="subject">Issued by default; it decides nothing without an allowlist.</param>
    /// <param name="subjectClaimType">
    /// The OIDC handler maps inbound claims unless told not to, so <c>sub</c> defaults to the
    /// ClaimTypes URI. Mapping off is the other spelling; both are live arms of
    /// <see cref="AdminAuthorization.IsOperator"/>.
    /// </param>
    public static HttpClient CreateOperatorClient(
        this WebApplicationFactory<KairosDbContext> app,
        Action<IServiceCollection>? substitutions = null,
        IReadOnlyList<string>? groups = null,
        string groupClaimType = AdminAuthorization.GroupClaimType,
        string? subject = Subject,
        string subjectClaimType = ClaimTypes.NameIdentifier) =>
        app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication()
                .AddScheme<SignedInOptions, SignedInHandler>(Scheme, options =>
                {
                    options.Groups = groups ?? [AdminAuthorization.DefaultGroup];
                    options.GroupClaimType = groupClaimType;
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
        public IReadOnlyList<string> Groups { get; set; } = [];

        public string GroupClaimType { get; set; } = AdminAuthorization.GroupClaimType;

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
            foreach (var group in Options.Groups)
            {
                claims.Add(new Claim(Options.GroupClaimType, group));
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
