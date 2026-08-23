namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// Whether a stored credential can still be used, as a property of the credential rather than of
/// the provider that discovered it. A second provider inherits this by existing.
/// </summary>
/// <remarks>
/// Two values, spaced like <see cref="IntegrationProvider"/> so a third can be inserted without a
/// migration. "Unknown" was considered and dropped: a credential that has been connected and
/// never used is <see cref="Healthy"/> with a null <see cref="IntegrationCredential.LastSuccessAt"/>,
/// and that pair already says "connected, never proven" without a third state every reader would
/// have to decide how to render.
/// </remarks>
public enum CredentialHealth
{
    /// <summary>Nothing has refused it. The state a new credential is written in.</summary>
    Healthy = 10,

    /// <summary>
    /// A refresh was refused outright — the grant was revoked or the account password changed.
    /// Nothing retries its way out of it; only a new consent clears it.
    /// </summary>
    ReconnectRequired = 20,
}
