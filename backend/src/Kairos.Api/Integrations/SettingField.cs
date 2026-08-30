using System.Text.Json;

namespace Kairos.Api.Integrations;

/// <summary>
/// One setting, described well enough for a renderer that knows no vendor to draw it.
/// </summary>
/// <param name="Options">
/// Null for <see cref="SettingFieldKind.Toggle"/>, which has no list to offer. Null for the other
/// two means the provider could not be reached — never "there are none to choose from". An empty
/// list is a provider that really has nothing.
/// </param>
/// <param name="MaxValues">
/// The cap on a <see cref="SettingFieldKind.MultiSelect"/>, null where there is none. Stated
/// generically so the renderer can stop a save that would be refused, and so the refusal reads
/// "you can choose at most fifty" rather than a vendor's sentence about freebusy.
/// </param>
/// <param name="Required">
/// Whether the field may be left empty. The calendar bookings are written to must resolve to
/// something; the set busy time is read from need not.
/// </param>
/// <param name="Value">
/// A bool for <see cref="SettingFieldKind.Toggle"/>, a string or null for
/// <see cref="SettingFieldKind.Select"/>, an array of strings for
/// <see cref="SettingFieldKind.MultiSelect"/>; for either of those two, every value is the
/// <see cref="SettingOption.Value"/> of one of this field's own <paramref name="Options"/>.
/// A <see cref="JsonElement"/> rather than three nullable properties of which exactly one is ever
/// set: the union is real, and spelling it as a wide record with two dead members every time would
/// be the dishonest half of the trade. What it costs instead is two failures that surface far from
/// their cause — <c>default(JsonElement)</c> is <c>ValueKind.Undefined</c> and throws from the
/// response writer rather than from the seam that built the field, and an element outliving its
/// <see cref="JsonDocument"/> throws <see cref="ObjectDisposedException"/>. Building every value
/// with <c>JsonSerializer.SerializeToElement(...)</c> avoids both, and the receiving side needs no
/// defensive <c>Clone()</c>, because <c>System.Text.Json</c> clones when it deserializes into a
/// <see cref="JsonElement"/>.
/// </param>
public sealed record SettingField(
    string Key,
    SettingFieldKind Kind,
    string Label,
    string? Help,
    IReadOnlyList<SettingOption>? Options,
    int? MaxValues,
    bool Required,
    JsonElement Value);
