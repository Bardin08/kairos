using System.Text.Json;

using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Security;

using Microsoft.EntityFrameworkCore;

using NodaTime;

namespace Kairos.Api.Integrations;

/// <summary>
/// The one door to <see cref="IntegrationCredential"/>. Rows are addressed by
/// <see cref="IntegrationProvider"/> — the table's unique index says one connected account per
/// provider — and the payload is generic, so each adapter asks for its own shape and no caller
/// ever writes a line of JSON or touches <see cref="SecretProtector"/>.
/// </summary>
/// <remarks>
/// The serialised payload exists as a local in <see cref="ReadAsync{TPayload}"/> and
/// <see cref="WriteAsync{TPayload}"/> and nowhere else in the process: it is never a field, never
/// a response, never a log argument. What crosses the boundary is a typed payload the provider
/// defined, which is what keeps "the refresh token" out of every layer that has no use for it.
/// </remarks>
public sealed class IntegrationCredentialStore(
    KairosDbContext context,
    SecretProtector protector,
    IClock clock)
{
    /// <summary>
    /// Every connected provider, for the page's status column. The rows carry ciphertext and the
    /// caller has no way to read it, which is why handing back the entity is safe here.
    /// </summary>
    public async Task<IReadOnlyList<IntegrationCredential>> ListAsync(CancellationToken cancellationToken) =>
        await context.IntegrationCredentials.AsNoTracking().ToListAsync(cancellationToken);

    /// <summary>Null when the provider has never been connected — not an error, a state.</summary>
    public async Task<TPayload?> ReadAsync<TPayload>(
        IntegrationProvider provider,
        CancellationToken cancellationToken)
        where TPayload : class
    {
        var row = await FindAsync(provider, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var json = protector.Unprotect(row.ProtectedPayload);
        return JsonSerializer.Deserialize<TPayload>(json)
            ?? throw new InvalidOperationException($"The stored {provider} credential is empty.");
    }

    /// <summary>
    /// Inserts or replaces the provider's one row. A null <paramref name="accountLabel"/> leaves
    /// whatever is there alone, so a caller rewriting the payload — a refresh that was refused,
    /// say — does not have to know the label to avoid erasing it.
    /// </summary>
    public async Task WriteAsync<TPayload>(
        IntegrationProvider provider,
        TPayload payload,
        string? accountLabel,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        var now = clock.GetCurrentInstant().ToUnixTimeSeconds();
        var row = await FindAsync(provider, cancellationToken);

        if (row is null)
        {
            row = new IntegrationCredential
            {
                Provider = provider,
                ProtectedPayload = string.Empty,
                ConnectedAt = now,
            };

            context.IntegrationCredentials.Add(row);
        }

        // Encrypted here rather than by the caller, so there is exactly one place that can get
        // it wrong and exactly one place to look when asking whether it was done at all.
        row.ProtectedPayload = protector.Protect(json);
        row.UpdatedAt = now;

        if (accountLabel is not null)
        {
            row.AccountLabel = accountLabel;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Tracked: the write path mutates what this returns.</summary>
    private Task<IntegrationCredential?> FindAsync(
        IntegrationProvider provider,
        CancellationToken cancellationToken) =>
        context.IntegrationCredentials.FirstOrDefaultAsync(
            row => row.Provider == provider, cancellationToken);
}
