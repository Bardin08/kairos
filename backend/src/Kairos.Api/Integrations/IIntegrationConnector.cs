using Kairos.Api.Persistence.Entities;

namespace Kairos.Api.Integrations;

/// <summary>
/// Everything above this line — the endpoints, and therefore the Integrations page — knows a
/// provider as an <see cref="IntegrationProvider"/> value and this shape, and never as a vendor's
/// type.
/// </summary>
/// <remarks>
/// Written with a single implementation on purpose. The alternative is endpoints that inject
/// GoogleClientFactory directly, and then the second provider is a change to the endpoints, to
/// the response body and to the page rather than one more registration in the composition root.
/// </remarks>
public interface IIntegrationConnector
{
    public IntegrationProvider Provider { get; }

    /// <summary>
    /// Reported to the page so an unconfigured provider renders as unconfigured, rather than as a
    /// Connect button that fails only once somebody presses it.
    /// </summary>
    public bool IsConfigured { get; }

    /// <summary>
    /// <paramref name="state"/> comes back on the callback untouched, which is the only thing
    /// tying the two halves of the flow together.
    /// </summary>
    public Uri BuildConsentUrl(string state);

    /// <summary>
    /// Stores the credential. Throws on anything that means the account is not connected — the
    /// caller is a browser redirect, so it ends on the page.
    /// </summary>
    public Task CompleteAsync(string code, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the provider to renew the stored grant and records the answer on the credential,
    /// healthy or withdrawn. Returns false only when the provider has withdrawn the grant, and true
    /// otherwise, including when it asks nothing because the provider is unconfigured, unconnected
    /// or paused.
    /// </summary>
    /// <remarks>
    /// While a cached access token is still fresh the provider is not asked, so a withdrawn grant
    /// surfaces within one access-token lifetime, not on this call. Only a withdrawn grant is
    /// swallowed; any other failure propagates unrecorded, so a network blip never reads as a revoke.
    /// </remarks>
    public Task<bool> RefreshAsync(CancellationToken cancellationToken);
}
