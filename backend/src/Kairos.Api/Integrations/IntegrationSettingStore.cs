using System.Collections.ObjectModel;
using System.Text.Json;

using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;

using NodaTime;

namespace Kairos.Api.Integrations;

/// <summary>
/// The one door to <see cref="IntegrationSetting"/>, so no caller writes a line of JSON. Values
/// cross this boundary as a map of field key to value — the shape
/// <see cref="IIntegrationSettings.SaveSettingsAsync"/> takes, so nothing has to be translated on
/// the way in.
/// </summary>
/// <remarks>
/// Merging rather than replacing on write, and not because a caller sends a delta: the operator's
/// Save sends the whole of the panel, exactly as
/// <see cref="IIntegrationSettings.SaveSettingsAsync"/> says it does. It merges because not every
/// writer is the operator — a provider materialising a default of its own writes the one key it
/// decided, and a replace would let that write delete the rest.
///
/// Removal is therefore spelled, since a merge cannot express it by omission: a value of
/// <see cref="JsonValueKind.Null"/> deletes its key rather than storing a null, so an unset
/// setting has one spelling — an absent key — and never two.
///
/// Safe against a second writer only in the sense that no writer clears another's keys by
/// omission. Two writes that overlap in time are not: both read the row, both merge onto what they
/// read, and the second commit carries none of the first's new keys. Nothing here has a
/// concurrency token, unlike <see cref="Pattern.OverridesVersion"/>, because every writer today is
/// one operator pressing Save or one default being materialised behind them — and a token would
/// need a column, a migration and a conflict the settings panel has no answer for. A writer that
/// runs on its own schedule is the change that makes it necessary.
/// </remarks>
public sealed class IntegrationSettingStore(
    KairosDbContext context,
    IServiceScopeFactory scopes,
    IClock clock)
{
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
            return ReadOnlyDictionary<string, JsonElement>.Empty;
        }

        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.FieldValues);
        if (values is null)
        {
            return ReadOnlyDictionary<string, JsonElement>.Empty;
        }

        return values;
    }

    /// <summary>
    /// Writes the keys in <paramref name="values"/> and leaves every other key as it was. A key
    /// whose value is <see cref="JsonValueKind.Null"/> is deleted instead of written.
    /// </summary>
    /// <remarks>
    /// On a context of its own, and that is this method's correctness rather than a preference.
    /// Materialising a default is a write on a READ path — <see cref="Google.GoogleCalendarAdapter"/>
    /// works one out mid-freebusy — and availability is read from inside
    /// <see cref="Features.Bookings.BookingMover.ReleaseAndCheckAsync"/>, where a booking has been
    /// released and something has to put it back. Sharing the caller's context, this commit is the
    /// caller's commit: it lands whatever they had staged and not yet decided to keep, and a failed
    /// insert leaves an Added row in their change tracker that every later
    /// <c>SaveChangesAsync</c> retries — so a settings write nobody asked for throws from
    /// <see cref="Features.Bookings.BookingMover.RestoreAsync"/> and the booking stays Cancelled on
    /// disk. Nothing is staged across that boundary today, which makes the first half a trap rather
    /// than a bug; the second half needs only the insert to lose a race against another first load.
    ///
    /// <see cref="Google.GoogleClientFactory"/> reaches this store's neighbour through a scope for
    /// the mirror-image reason: it is a singleton and has no request context to borrow. Here there
    /// is one, and borrowing it is the fault.
    ///
    /// What a separate context costs is that the caller sees this write only by reading again —
    /// true already, since <see cref="ReadAsync"/> is <c>AsNoTracking</c> — and that it commits
    /// alone, which <see cref="Features.Integrations.IntegrationSettingsWriter"/> already documents
    /// as the settings panel's behaviour.
    /// </remarks>
    public async Task WriteAsync(
        IntegrationProvider provider,
        IReadOnlyDictionary<string, JsonElement> values,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var isolated = scope.ServiceProvider.GetRequiredService<KairosDbContext>();

        var row = await isolated.IntegrationSettings
            .FirstOrDefaultAsync(candidate => candidate.Provider == provider, cancellationToken);

        var merged = row is null
            ? new Dictionary<string, JsonElement>()
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.FieldValues)
              ?? new Dictionary<string, JsonElement>();

        foreach (var (key, value) in values)
        {
            if (value.ValueKind == JsonValueKind.Null)
            {
                merged.Remove(key);
                continue;
            }

            merged[key] = value;
        }

        var json = JsonSerializer.Serialize(merged);
        var now = clock.GetCurrentInstant().ToUnixTimeSeconds();

        if (row is null)
        {
            isolated.IntegrationSettings.Add(new IntegrationSetting
            {
                Provider = provider,
                FieldValues = json,
                UpdatedAt = now,
            });
        }
        else
        {
            row.FieldValues = json;
            row.UpdatedAt = now;
        }

        await isolated.SaveChangesAsync(cancellationToken);
    }
}
