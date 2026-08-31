using Kairos.Api.Integrations;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Shared;

namespace Kairos.Api.Features.Integrations;

/// <summary>
/// A provider name off a route turned into a connector, and a provider turned into the credential
/// row that proves somebody connected it. Every route under <c>/integrations/{provider}</c> asks
/// the first question — the connect and callback routes in <see cref="IntegrationEndpoints"/> as
/// much as the settings routes — and neither question is a route's business to answer.
/// </summary>
/// <remarks>
/// The connect route matched the name itself until this class took the match over. Two copies of
/// one case-insensitive comparison over one set of connectors is a rename of the wire spelling that
/// needs two edits with no compiler between them, and the spelling is a URL somebody has
/// bookmarked.
/// </remarks>
public sealed class IntegrationProviderResolver(
    IEnumerable<IIntegrationConnector> connectors,
    IntegrationCredentialStore credentials)
{
    /// <summary>What a name no connector answers to is called, wherever it is refused.</summary>
    public const string UnknownProviderName = "Integration provider";

    /// <summary>
    /// Matched against the registered connectors rather than parsed into
    /// <see cref="IntegrationProvider"/> by the binder, for two reasons found by running it: the
    /// binder's enum parse is case-sensitive, so <c>/integrations/google/connect</c> did not bind at
    /// all, and a binder rejection is an empty 400 where an unimplemented provider owes a named
    /// answer. It also keeps the enum's numeric spelling, which <c>Enum.TryParse</c> accepts, from
    /// being a second name for a route.
    /// </summary>
    /// <remarks>
    /// Nullable, for the callback route alone: a browser arrives there by navigation, and
    /// ProblemDetails in the address bar is a dead end with nothing on it to press. Everything a
    /// fetch calls uses <see cref="Require"/>.
    /// </remarks>
    public IIntegrationConnector? Find(string provider) =>
        connectors.FirstOrDefault(connector =>
            Name(connector.Provider).Equals(provider, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// For the routes a fetch calls, where "no such provider" is a 404 with <c>not_found</c> on it
    /// that the page can act on.
    /// </summary>
    public IIntegrationConnector Require(string provider) =>
        Find(provider) ?? throw DomainException.NotFound(UnknownProviderName);

    /// <summary>The provider behind the name, for a caller with no use for the connector itself.</summary>
    public IntegrationProvider Resolve(string provider) => Require(provider).Provider;

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
            ?? throw DomainException.NotFound(IntegrationCredentialStore.MissingConnectionName);
    }

    /// <summary>Lowercase, as every other enum this API returns — booking status set the shape.</summary>
    public static string Name(IntegrationProvider provider) =>
        provider.ToString().ToLowerInvariant();
}
