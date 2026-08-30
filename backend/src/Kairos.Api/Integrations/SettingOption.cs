namespace Kairos.Api.Integrations;

/// <summary>One choice a field offers.</summary>
/// <param name="Detail">
/// The second line under the option, or null. Where a provider needs to say something about a
/// particular choice — "read-only", "no longer reachable" — it says it here rather than by
/// decorating the label, so the renderer keeps one typographic treatment for every provider.
/// </param>
public sealed record SettingOption(string Value, string Label, string? Detail);
