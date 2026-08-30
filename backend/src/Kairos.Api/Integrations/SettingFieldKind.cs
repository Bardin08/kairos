namespace Kairos.Api.Integrations;

/// <summary>
/// The closed vocabulary. A kind the generic renderer cannot draw is a kind no provider may
/// declare — which is the whole of why settings are described rather than shipped as a panel per
/// vendor.
/// </summary>
/// <remarks>Spaced like <see cref="Persistence.Entities.IntegrationProvider"/>.</remarks>
public enum SettingFieldKind
{
    Toggle = 10,
    Select = 20,
    MultiSelect = 30,
}
