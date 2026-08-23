using System.Collections.Immutable;

using Kairos.Api.Features.Integrations;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Shared;

using NodaTime;

namespace Kairos.Api.Integrations;

/// <summary>
/// Every connected provider whose credential has stopped working, as alerts.
/// </summary>
/// <remarks>
/// Read this file's usings when asking what a second provider costs: there is no vendor namespace
/// among them, and adding one would be the bug. It reads a column and a label — nothing is
/// decrypted, no adapter is called, and a provider that did not exist when this was written
/// produces an alert by having a row.
/// </remarks>
public sealed class IntegrationAlertSource(IntegrationCredentialStore store) : IOperatorAlertSource
{
    /// <summary>
    /// The grant is gone and only a new consent brings it back.
    /// </summary>
    /// <remarks>
    /// Details it may carry, and the whole list: <c>account</c>, the stored account label, absent
    /// when the credential has none. Documented here because the screen reads only the keys its
    /// copy names, and an undocumented key is one no screen will ever read.
    /// </remarks>
    public const string ReconnectRequiredCode = "integration_reconnect_required";

    private const string AccountKey = "account";

    public async Task<IReadOnlyList<OperatorAlert>> CollectAsync(CancellationToken cancellationToken)
    {
        var credentials = await store.ListAsync(cancellationToken);
        var alerts = new List<OperatorAlert>();

        foreach (var credential in credentials.Where(row => row.Health != CredentialHealth.Healthy))
        {
            alerts.Add(new OperatorAlert(
                ReconnectRequiredCode,
                AlertSeverity.Error,
                credential.Provider.ToString().ToLowerInvariant(),
                credential.LastSuccessAt is null
                    ? null
                    : Instant.FromUnixTimeSeconds(credential.LastSuccessAt.Value),
                // The existing constant rather than a second copy of "/integrations": that path is
                // registered byte-for-byte with a provider, so it has exactly one spelling.
                IntegrationEndpoints.ReturnPath,
                credential.AccountLabel is null
                    ? ImmutableDictionary<string, string>.Empty
                    : ImmutableDictionary<string, string>.Empty.Add(AccountKey, credential.AccountLabel)));
        }

        return alerts;
    }
}
