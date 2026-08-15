using System.Security.Cryptography;

using Kairos.Api.Integrations;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Security;
using Kairos.Api.Shared;

using NodaTime;
using NodaTime.Text;

namespace Kairos.Api.Features.Integrations;

/// <summary>
/// One provider as the Integrations page renders it. Every field is provider-neutral, which is
/// what lets the page list whatever the backend returns instead of hard-coding a Google button.
/// </summary>
public sealed record IntegrationResponse(
    string Provider,
    bool Configured,
    bool Connected,
    string? AccountLabel,
    string? ConnectedAt,
    bool ReconnectRequired);

/// <summary>Where to send the browser. The page navigates to it; it does not fetch it.</summary>
public sealed record ConnectIntegrationResponse(string ConsentUrl);

/// <summary>
/// List providers, start a connection, finish a connection. Which provider, and what its consent
/// screen wants, lives under <c>Integrations/</c> — no vendor name reaches this file.
/// </summary>
/// <remarks>
/// <para>
/// <b>What protects these endpoints.</b> The two operator-driven routes hang off
/// <see cref="AdminGroup.MapAdmin"/> like every other admin route, and are therefore under
/// <c>/api/v1/admin</c> — Part B wrote them "behind the admin boundary" before there was one, and
/// this is where that promise is kept. They were left outside the prefix at first to protect the
/// callback's registered URI, but that constraint binds the callback alone: <c>list</c> and
/// <c>connect</c> are registered with nobody and cost two fetch URLs to move. The callback stays
/// exactly where it is and stays anonymous, because a browser arrives there on a redirect from
/// Google and the ticket it presents is <c>state</c>.
/// </para>
/// <para>
/// <b>state.</b> Not a row in a table and not a cache entry: the value is the provider and an
/// expiry, sealed with the same AES-GCM key that protects the credentials. Forging one needs the
/// key, replaying one needs to happen inside <see cref="StateLifetimeMinutes"/> minutes, and
/// restarting the process mid-consent does not strand the operator. Nothing to store and nothing
/// to expire.
/// </para>
/// </remarks>
public static class IntegrationEndpoints
{
    /// <summary>
    /// The page the callback lands on, whatever happened. A human is holding this browser, so the
    /// flow ends where it started, carrying <c>?connected={provider}</c> or <c>?error={code}</c>.
    /// Not configurable: the SPA is served by this application at this path, so the two halves
    /// live in one repository and an operator has nothing to point elsewhere.
    /// </summary>
    public const string ReturnPath = "/integrations";

    /// <summary>The provider exists but this deployment has no client credentials for it.</summary>
    public const string ProviderNotConfiguredCode = "provider_not_configured";

    /// <summary>The operator pressed Cancel on the consent screen, or the provider refused.</summary>
    public const string ConsentDeniedCode = "consent_denied";

    /// <summary>The callback's <c>state</c> was absent, forged, expired, or for another provider.</summary>
    public const string StateInvalidCode = "state_invalid";

    /// <summary>The exchange failed. The reason is in the log; there is nothing safe to put here.</summary>
    public const string ConnectFailedCode = "connect_failed";

    private const string ConnectedParameter = "connected";
    private const string ErrorParameter = "error";
    private const char StateSeparator = ':';
    private const int StateLifetimeMinutes = 10;

    /// <summary>
    /// The operator's two, on the admin group — so the policy comes from the group rather than
    /// from a <c>RequireAuthorization</c> beside each route that a third route could forget.
    /// </summary>
    public static RouteGroupBuilder MapIntegrationsAdmin(this RouteGroupBuilder group)
    {
        group.MapGet("/integrations", ListAsync).WithName("ListIntegrations");
        group.MapPost("/integrations/{provider}/connect", Connect).WithName("ConnectIntegration");
        return group;
    }

    /// <summary>
    /// The one route that cannot move and cannot be gated.
    /// </summary>
    /// <remarks>
    /// Its path is registered byte-for-byte with the provider, so changing it breaks consent
    /// rather than protecting it, and the caller is a browser arriving on a redirect with no
    /// session of ours — which is why the ticket it presents is <c>state</c>.
    /// </remarks>
    public static RouteGroupBuilder MapIntegrationCallback(this RouteGroupBuilder group)
    {
        group.MapGet("/integrations/{provider}/callback", CallbackAsync)
            .WithName("CompleteIntegration");

        return group;
    }

    private static async Task<IReadOnlyList<IntegrationResponse>> ListAsync(
        IEnumerable<IIntegrationConnector> connectors,
        IntegrationCredentialStore store,
        CancellationToken cancellationToken)
    {
        // Connectors rather than every value of the enum: the page should offer what this build
        // can actually connect, not a button that answers 404.
        var connections = await store.ListAsync(cancellationToken);
        var responses = new List<IntegrationResponse>();

        foreach (var connector in connectors.OrderBy(candidate => candidate.Provider))
        {
            var row = connections.FirstOrDefault(candidate => candidate.Provider == connector.Provider);
            var reconnect = row is not null && await connector.IsReconnectRequiredAsync(cancellationToken);

            responses.Add(new IntegrationResponse(
                Name(connector.Provider),
                connector.IsConfigured,
                row is not null,
                row?.AccountLabel,
                row is null ? null : Format(row.ConnectedAt),
                reconnect));
        }

        return responses;
    }

    private static ConnectIntegrationResponse Connect(
        string provider,
        IEnumerable<IIntegrationConnector> connectors,
        SecretProtector protector,
        IClock clock)
    {
        var connector = Require(connectors, provider);
        if (!connector.IsConfigured)
        {
            throw DomainException.Invalid(
                ProviderNotConfiguredCode,
                "This Kairos has no client credentials for that provider.");
        }

        return new ConnectIntegrationResponse(
            connector.BuildConsentUrl(MintState(protector, connector.Provider, clock)).ToString());
    }

    private static async Task<IResult> CallbackAsync(
        string provider,
        string? code,
        string? state,
        string? error,
        IEnumerable<IIntegrationConnector> connectors,
        SecretProtector protector,
        IClock clock,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var connector = Require(connectors, provider);
        var logger = loggers.CreateLogger(typeof(IntegrationEndpoints));
        var known = connector.Provider;

        if (!string.IsNullOrEmpty(error))
        {
            // Logged, never echoed. It is attacker-controllable text arriving on a route anyone
            // can call, and the page has exactly one useful thing to say about all of it.
            logger.LogWarning("{Provider} did not grant consent: {Error}", known, error);
            return RedirectToPage(ErrorParameter, ConsentDeniedCode);
        }

        if (!IsStateValid(protector, known, state, clock))
        {
            logger.LogWarning("Discarded a {Provider} callback whose state did not verify.", known);
            return RedirectToPage(ErrorParameter, StateInvalidCode);
        }

        if (string.IsNullOrEmpty(code))
        {
            logger.LogWarning("A verified {Provider} callback carried no authorization code.", known);
            return RedirectToPage(ErrorParameter, ConnectFailedCode);
        }

        try
        {
            await connector.CompleteAsync(code, cancellationToken);
        }
        catch (Exception exception)
        {
            // Everything past this line is a conversation with someone else's server — a refused
            // exchange, an API that was never enabled on the project, a network that was not
            // there. None of it is worth a stack trace in a browser, and all of it is worth one
            // in the log.
            logger.LogError(exception, "Completing the {Provider} connection failed.", known);
            return RedirectToPage(ErrorParameter, ConnectFailedCode);
        }

        return RedirectToPage(ConnectedParameter, Name(known));
    }

    /// <summary>
    /// The route segment is matched against the connectors rather than parsed into
    /// <see cref="IntegrationProvider"/> by the binder, for two reasons found by running it:
    /// the binder's enum parse is case-sensitive, so <c>/integrations/google/connect</c> — the
    /// only casing a URL should have — did not bind at all; and a binder rejection is an empty
    /// 400, where an unimplemented provider is supposed to be the 404 with <c>not_found</c> that
    /// the page's Connect button deserves. Matching the connectors also means the numeric
    /// spelling of the enum, which <c>Enum.TryParse</c> accepts, is not a second name for a
    /// route.
    /// </summary>
    private static IIntegrationConnector Require(
        IEnumerable<IIntegrationConnector> connectors,
        string provider) =>
        connectors.FirstOrDefault(connector =>
            Name(connector.Provider).Equals(provider, StringComparison.OrdinalIgnoreCase))
        ?? throw DomainException.NotFound("Integration provider");

    private static string MintState(SecretProtector protector, IntegrationProvider provider, IClock clock)
    {
        var expiry = clock.GetCurrentInstant().Plus(Duration.FromMinutes(StateLifetimeMinutes));
        return protector.Protect($"{provider}{StateSeparator}{expiry.ToUnixTimeSeconds()}");
    }

    private static bool IsStateValid(
        SecretProtector protector,
        IntegrationProvider provider,
        string? state,
        IClock clock)
    {
        if (string.IsNullOrEmpty(state))
        {
            return false;
        }

        string plain;
        try
        {
            // Base64's '+' survives a correctly encoded round trip and decodes to a space in the
            // one that is not. Restoring it costs a character comparison and removes a class of
            // bug that would only ever appear against the real authorization server.
            plain = protector.Unprotect(state.Replace(' ', '+'));
        }
        catch (Exception exception)
            when (exception is CryptographicException
                or FormatException
                or ArgumentException
                or OverflowException)
        {
            return false;
        }

        var parts = plain.Split(StateSeparator);
        return parts.Length == 2
            && parts[0] == provider.ToString()
            && long.TryParse(parts[1], out var expiry)
            && clock.GetCurrentInstant().ToUnixTimeSeconds() < expiry;
    }

    private static IResult RedirectToPage(string parameter, string value) =>
        Results.Redirect($"{ReturnPath}?{parameter}={Uri.EscapeDataString(value)}");

    /// <summary>Lowercase, as every other enum this API returns — booking status set the shape.</summary>
    private static string Name(IntegrationProvider provider) =>
        provider.ToString().ToLowerInvariant();

    private static string Format(long unixSeconds) =>
        InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(unixSeconds));
}
