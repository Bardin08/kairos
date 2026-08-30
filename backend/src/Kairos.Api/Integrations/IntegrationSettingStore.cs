using System.Text.Json;

using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;

using NodaTime;

namespace Kairos.Api.Integrations;

/// <summary>
/// The one door to <see cref="IntegrationSetting"/>, so no caller writes a line of JSON. Values
/// cross this boundary as a map of field key to value — the shape
/// <see cref="IIntegrationSettings.SaveSettingsAsync"/> takes and <see cref="SettingField"/>
/// carries, so nothing has to be translated on the way in.
/// </summary>
/// <remarks>
/// Merging rather than replacing on write. A caller saves the keys it knows about and says
/// nothing about the rest, so a replace would let whichever wrote last delete settings it had
/// never heard of.
/// </remarks>
public sealed class IntegrationSettingStore(KairosDbContext context, IClock clock)
{
    private static readonly IReadOnlyDictionary<string, JsonElement> Empty =
        new Dictionary<string, JsonElement>();

    /// <summary>
    /// Empty when the provider has never been configured — not an error, a state, and the one the
    /// default is computed for.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, JsonElement>> ReadAsync(
        IntegrationProvider provider,
        CancellationToken cancellationToken)
    {
        var row = await context.IntegrationSettings.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Provider == provider, cancellationToken);

        if (row is null)
        {
            return Empty;
        }

        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.Values) ?? Empty;
    }

    public async Task WriteAsync(
        IntegrationProvider provider,
        IReadOnlyDictionary<string, JsonElement> values,
        CancellationToken cancellationToken)
    {
        var row = await context.IntegrationSettings
            .FirstOrDefaultAsync(candidate => candidate.Provider == provider, cancellationToken);

        var merged = row is null
            ? new Dictionary<string, JsonElement>()
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.Values)
              ?? new Dictionary<string, JsonElement>();

        foreach (var (key, value) in values)
        {
            merged[key] = value;
        }

        var json = JsonSerializer.Serialize(merged);
        var now = clock.GetCurrentInstant().ToUnixTimeSeconds();

        if (row is null)
        {
            context.IntegrationSettings.Add(new IntegrationSetting
            {
                Provider = provider,
                Values = json,
                UpdatedAt = now,
            });
        }
        else
        {
            row.Values = json;
            row.UpdatedAt = now;
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
