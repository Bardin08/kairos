namespace Kairos.Api.Integrations;

/// <summary>
/// The closed vocabulary. A kind the generic renderer cannot draw is a kind no provider may
/// declare — which is the whole of why settings are described rather than shipped as a panel per
/// vendor.
/// </summary>
/// <remarks>
/// Spaced like <see cref="Persistence.Entities.IntegrationProvider"/>. Nothing stores this, so the
/// numbers buy no migration safety; they buy the habit, and a diff that adds a kind between two
/// others renumbers nothing.
/// </remarks>
public enum SettingFieldKind
{
    Toggle = 10,
    Select = 20,
    MultiSelect = 30,
}
