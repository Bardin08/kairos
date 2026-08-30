using System.Text.Json;

using Kairos.Api.Integrations;
using Kairos.Api.Shared;

namespace Kairos.Api.Features.Integrations;

/// <summary>
/// One provider's settings, saved. The switch is written here and every other key is handed to
/// the provider, which is what decides whether a value is legal.
/// </summary>
public sealed class IntegrationSettingsWriter(
    IntegrationProviderResolver providers,
    IEnumerable<IIntegrationSettings> capabilities,
    IntegrationCredentialStore credentials)
{
    public async Task SaveAsync(
        string provider,
        IReadOnlyDictionary<string, JsonElement> values,
        CancellationToken cancellationToken)
    {
        var known = providers.Resolve(provider);
        await providers.RequireConnectedAsync(known, cancellationToken);

        // Peeled off before the provider sees the map. The switch is not the provider's to
        // validate, and a provider with no settings at all must still be pausable.
        var rest = new Dictionary<string, JsonElement>(values);
        var switched = rest.Remove(IIntegrationSettings.EnabledKey, out var enabled);

        if (switched && (enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
        {
            throw DomainException.Invalid(
                IIntegrationSettings.SettingInvalidCode,
                $"'{IIntegrationSettings.EnabledKey}' takes true or false.");
        }

        if (rest.Count > 0)
        {
            var capability = capabilities.FirstOrDefault(candidate => candidate.Provider == known)
                ?? throw DomainException.Invalid(
                    IIntegrationSettings.SettingUnknownCode,
                    "That provider has no settings of its own.");

            await capability.SaveSettingsAsync(rest, cancellationToken);
        }

        if (switched)
        {
            // Last, and only once the provider has taken the rest. The two stores commit
            // separately, so paused first left the integration genuinely off behind a 400 the
            // panel rendered as a failed save — one Save, half applied.
            await credentials.SetEnabledAsync(known, enabled.GetBoolean(), cancellationToken);
        }
    }
}
