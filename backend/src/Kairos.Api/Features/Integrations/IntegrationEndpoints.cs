using System.Security.Cryptography;

using Kairos.Api.Integrations;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Security;
using Kairos.Api.Shared;

using NodaTime;
using NodaTime.Text;

namespace Kairos.Api.Features.Integrations;

/// <summary>
/// One provider as the Integrations page renders it. Every field is provider-neutral, so the page
/// lists whatever the backend returns instead of hard-coding a Google button.
/// </summary>
/// <param name="Enabled">
/// The operator's switch. False is a stored credential nothing is read or written with, which is
/// not <see cref="Connected"/> being false — there the grant is gone and the row with it. True for
/// a provider nobody connected, because a connection that does not exist is not one somebody
/// paused.
/// </param>
/// <param name="Fault">
/// A code for a stored credential this deployment cannot use, null when there is nothing wrong. A
/// code and never a sentence: the page turns codes into copy everywhere else, and prose from the
/// server is prose no designer ever sees.
/// </param>
public sealed record IntegrationResponse(
    string Provider,
    bool Configured,
    bool Connected,
    string? AccountLabel,
    string? ConnectedAt,
    bool ReconnectRequired,
    bool Enabled,
    string? Fault);

/// <summary>Where to send the browser. The page navigates to it; it does not fetch it.</summary>
public sealed record ConnectIntegrationResponse(string ConsentUrl);

/// <summary>
/// No vendor name reaches this file: which provider exists, and what its consent screen wants,
/// lives under <c>Integrations/</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What protects these endpoints.</b> The two operator-driven routes hang off
/// <see cref="AdminGroup.MapAdmin"/> under <c>/api/v1/admin</c>. Only the callback's registered URI
/// is fixed — the other two cost two fetch URLs to move — so it alone stays outside the prefix and
/// anonymous: a browser arrives on a redirect from the provider, and its ticket is <c>state</c>.
/// </para>
/// <para>
/// <b>state.</b> No row and no cache entry: the provider and an expiry, sealed with the AES-GCM key
/// that protects the credentials. Forging one needs the key, replaying one has
/// <see cref="StateLifetimeMinutes"/> minutes, and a restart mid-consent strands nobody.
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
    /// The one route that cannot move and cannot be gated: its path is registered byte-for-byte
    /// with the provider, so changing it breaks consent rather than protecting it, and the caller
    /// is a browser on a redirect with no session of ours. Its ticket is <c>state</c>.
    /// </summary>
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
            var reconnect = false;
            string? fault = null;

            if (row is not null)
            {
                // The row's own column now, rather than a question put to the vendor's adapter
                // about a fact the database holds. A second provider gains this by having a row.
                reconnect = row.Health == CredentialHealth.ReconnectRequired;

                try
                {
                    // Asked outright. It used to fall out of a decrypt performed for another
                    // reason, so deleting that call would have taken this diagnosis with it —
                    // silently, because a page that renders is a page that looks fine.
                    await store.EnsureReadableAsync(connector.Provider, cancellationToken);
                }
                catch (DomainException failure)
                    when (failure.Code == IntegrationCredentialStore.CredentialUnreadableCode)
                {
                    // One provider's unusable credential is not the listing's failure. Answered on
                    // the row rather than thrown: the fix is reconnecting, the Connect button is
                    // what does it, and that button only exists if the table gets drawn. Thrown,
                    // this left the operator a sentence naming a key mismatch and nothing to press.
                    fault = failure.Code;
                }
            }

            responses.Add(new IntegrationResponse(
                IntegrationProviderResolver.Name(connector.Provider),
                connector.IsConfigured,
                row is not null,
                row?.AccountLabel,
                row is null ? null : Format(row.ConnectedAt),
                reconnect,
                row is null || row.Enabled,
                fault));
        }

        return responses;
    }

    private static ConnectIntegrationResponse Connect(
        string provider,
        IntegrationProviderResolver providers,
        SecretProtector protector,
        IClock clock)
    {
        var connector = providers.Require(provider);
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
        IntegrationProviderResolver providers,
        SecretProtector protector,
        IClock clock,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var logger = loggers.CreateLogger(typeof(IntegrationEndpoints));

        // Resolved through the nullable lookup rather than the throwing one the two operator routes
        // use. Those are fetched and can be told about a 404; this one is a page navigation, and
        // ProblemDetails in the address bar is a dead end with nothing on it to press. The code is
        // the same not_found the other would have thrown — the page renders a sentence for a code it
        // does not recognise, which is what makes that safe.
        var connector = providers.Find(provider);
        if (connector is null)
        {
            logger.LogWarning("Discarded a callback for a provider this build has no connector for.");
            return RedirectToPage(ErrorParameter, DomainException.NotFoundCode);
        }

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

        return RedirectToPage(ConnectedParameter, IntegrationProviderResolver.Name(known));
    }

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

    private static string Format(long unixSeconds) =>
        InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(unixSeconds));
}
