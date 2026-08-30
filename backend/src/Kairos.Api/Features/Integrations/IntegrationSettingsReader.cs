using System.Text.Json;

using Kairos.Api.Integrations;

namespace Kairos.Api.Features.Integrations;

/// <summary>
/// Everything the settings panel is told about one provider: the universal switch, then whatever
/// that provider describes for itself.
/// </summary>
/// <remarks>
/// The switch is added here rather than by any adapter, which is what makes the experience one
/// experience: a provider implementing no <see cref="IIntegrationSettings"/> at all still has a
/// panel, and a provider implementing it cannot contradict the switch or forget it.
/// </remarks>
public sealed class IntegrationSettingsReader(
    IntegrationProviderResolver providers,
    IEnumerable<IIntegrationSettings> capabilities)
{
    private const string EnabledLabel = "Use this integration";

    private const string EnabledHelp =
        "Turned off, Kairos keeps the connection but stops reading or writing anything.";

    public async Task<IntegrationSettingsResponse> ReadAsync(
        string provider,
        CancellationToken cancellationToken)
    {
        var known = providers.Resolve(provider);
        var row = await providers.RequireConnectedAsync(known, cancellationToken);
        var capability = capabilities.FirstOrDefault(candidate => candidate.Provider == known);

        var fields = new List<SettingFieldResponse> { Switch(row.Enabled) };

        if (capability is not null)
        {
            foreach (var field in await capability.DescribeSettingsAsync(cancellationToken))
            {
                if (field.Key == IIntegrationSettings.EnabledKey)
                {
                    // A provider's own bug, and one that would otherwise show as a field quietly
                    // missing from a panel. Thrown rather than skipped: the panel renders, the
                    // operator saves, and the setting they thought they changed was never there.
                    throw new InvalidOperationException(
                        $"The {known} adapter describes a field keyed "
                        + $"'{IIntegrationSettings.EnabledKey}', which is reserved.");
                }

                fields.Add(Describe(field));
            }
        }

        return new IntegrationSettingsResponse(IntegrationProviderResolver.Name(known), fields);
    }

    private static SettingFieldResponse Switch(bool enabled) =>
        new(
            IIntegrationSettings.EnabledKey,
            IntegrationSettingKinds.Spell(SettingFieldKind.Toggle),
            EnabledLabel,
            EnabledHelp,
            Options: null,
            MaxValues: null,
            Required: false,
            JsonSerializer.SerializeToElement(enabled));

    private static SettingFieldResponse Describe(SettingField field) =>
        new(
            field.Key,
            IntegrationSettingKinds.Spell(field.Kind),
            field.Label,
            field.Help,
            field.Options,
            field.MaxValues,
            field.Required,
            field.Value);
}
