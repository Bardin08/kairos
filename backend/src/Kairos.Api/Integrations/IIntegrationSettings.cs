using System.Text.Json;

using Kairos.Api.Persistence.Entities;

namespace Kairos.Api.Integrations;

/// <summary>
/// A provider that has something for the operator to configure. It follows the rule
/// <see cref="IntegrationProvider"/> states for every other capability: what a provider can do is
/// answered by which interfaces its adapter implements, never by a method that returns nothing for
/// providers that have nothing.
/// </summary>
/// <remarks>
/// The universal on/off switch is deliberately absent. It becomes a column on
/// <see cref="IntegrationCredential"/>, injected into the described list by the endpoint, so a
/// provider implementing none of this still has one and a provider implementing it cannot forget
/// or contradict it. <c>enabled</c> is therefore a reserved key here.
/// </remarks>
public interface IIntegrationSettings
{
    /// <summary>
    /// The universal switch's key, reserved. It is the contract's own — no provider may describe a
    /// field by that name — so it lives here rather than in whichever type happened to need it
    /// first.
    /// </summary>
    public const string EnabledKey = "enabled";

    public IntegrationProvider Provider { get; }

    /// <summary>
    /// The provider's own fields, each carrying its current value and its live options. Reaching
    /// the provider is expected to fail sometimes; a field whose options could not be read comes
    /// back with null options rather than throwing, because the saved value is still worth showing
    /// and the switch beside it is still worth offering.
    /// </summary>
    public Task<IReadOnlyList<SettingField>> DescribeSettingsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Validates and persists. Throws <see cref="Shared.DomainException"/> on anything illegal, so
    /// the endpoint above writes no validation branch — the division
    /// <see cref="Features.Integrations.IntegrationEndpoints.Connect"/> already uses.
    /// </summary>
    /// <param name="values">
    /// The whole of the provider's own settings as the operator is looking at them, never a delta:
    /// the panel has one Save and sends back everything it is showing. Never
    /// <see cref="EnabledKey"/> — the endpoint peels the switch off and writes the column before
    /// the provider is called, so a provider neither sees it nor can contradict it. A key that is
    /// absent is left as it was: the store merges rather than replaces, so one caller's keys never
    /// clear another's.
    /// </param>
    public Task SaveSettingsAsync(
        IReadOnlyDictionary<string, JsonElement> values,
        CancellationToken cancellationToken);
}
