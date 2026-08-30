using Kairos.Api.Integrations;

namespace Kairos.Api.Features.Integrations;

/// <summary>
/// The three strings the panel keys on, in one place.
/// </summary>
/// <remarks>
/// A switch rather than a serializer naming policy: a policy spells every enum in the API at once,
/// so a change made for one response silently rewrites the others. Here a reader sees all three
/// together and a rename cannot happen by accident.
/// </remarks>
public static class IntegrationSettingKinds
{
    public static string Spell(SettingFieldKind kind) => kind switch
    {
        SettingFieldKind.Toggle => "toggle",
        SettingFieldKind.Select => "select",
        SettingFieldKind.MultiSelect => "multiSelect",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown setting field kind."),
    };
}
