using Kairos.Api.Integrations;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Shared;

namespace Kairos.Api.Features.Integrations;

/// <summary>
/// A provider name off a route turned into a provider, and a provider turned into the credential
/// row that proves somebody connected it. Both questions are asked by
/// <see cref="IntegrationSettingsReader"/> and <see cref="IntegrationSettingsWriter"/>, and
/// neither is a route's business to answer.
/// </summary>
public sealed class IntegrationProviderResolver(
    IEnumerable<IIntegrationConnector> connectors,
    IntegrationCredentialStore credentials)
{
    private const string ProviderName = "Integration";

    /// <summary>
    /// Matched against the registered connectors, case-insensitively, for the reasons
    /// <see cref="IntegrationEndpoints"/> documents: the enum binder is case-sensitive, so
    /// <c>/integrations/google/settings</c> would not bind at all, and a binder rejection is an
    /// empty 400 where an unknown provider owes a named answer.
    /// </summary>
    public IntegrationProvider Resolve(string provider) =>
        connectors
            .FirstOrDefault(connector =>
                Name(connector.Provider).Equals(provider, StringComparison.OrdinalIgnoreCase))
            ?.Provider
        ?? throw DomainException.NotFound(ProviderName);

    /// <summary>
    /// The switch is a column on the credential row, so a provider with no row has nowhere to keep
    /// it — and nothing to configure either.
    /// </summary>
    public async Task<IntegrationCredential> RequireConnectedAsync(
        IntegrationProvider provider,
        CancellationToken cancellationToken)
    {
        var rows = await credentials.ListAsync(cancellationToken);

        return rows.FirstOrDefault(candidate => candidate.Provider == provider)
            ?? throw DomainException.NotFound(ProviderName);
    }

    /// <summary>Lowercase, as every other enum this API returns — booking status set the shape.</summary>
    public static string Name(IntegrationProvider provider) =>
        provider.ToString().ToLowerInvariant();
}
