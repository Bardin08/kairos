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
    /// <summary>
    /// The sentence <see cref="IntegrationEndpoints"/> answers for the same condition, held here
    /// so the connect route and the settings routes cannot drift into naming it differently.
    /// </summary>
    public const string UnknownProviderName = "Integration provider";

    private const string ConnectionName = "A connection to that integration";

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
        ?? throw DomainException.NotFound(UnknownProviderName);

    /// <summary>
    /// The switch is a column on the credential row, so a provider with no row has nowhere to keep
    /// it — and nothing to configure either.
    /// </summary>
    public async Task<IntegrationCredential> RequireConnectedAsync(
        IntegrationProvider provider,
        CancellationToken cancellationToken)
    {
        var rows = await credentials.ListAsync(cancellationToken);

        // Its own sentence, not the unknown provider's: a provider this build cannot name and one
        // nobody has connected are different things to do about, and the code is not_found for
        // both, so the title is all an operator reading a log has to tell them apart by.
        return rows.FirstOrDefault(candidate => candidate.Provider == provider)
            ?? throw DomainException.NotFound(ConnectionName);
    }

    /// <summary>Lowercase, as every other enum this API returns — booking status set the shape.</summary>
    public static string Name(IntegrationProvider provider) =>
        provider.ToString().ToLowerInvariant();
}
