using System.Security.Cryptography;
using System.Text.Json;

using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Security;
using Kairos.Api.Shared;

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
    /// A stored credential that this deployment's key cannot open. Named because it is the one
    /// failure here an operator can act on, and the action is on a button they are already
    /// looking at.
    /// </summary>
    public const string CredentialUnreadableCode = "credential_unreadable";

    /// <summary>
    /// How stale a proven success may get before a fresh one is worth a write. The success path
    /// runs on every token refresh, and a token refresh runs on every booking page load — so an
    /// unthrottled stamp is a SQLite write per page view for a value nobody read. Five minutes is
    /// far finer than any screen renders this to.
    /// </summary>
    private const int SuccessRefreshSeconds = 300;

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

        string json;
        try
        {
            json = protector.Unprotect(row.ProtectedPayload);
        }
        catch (Exception exception)
            when (exception is CryptographicException
                or FormatException
                or ArgumentException
                or OverflowException)
        {
            // AES-GCM authenticates, so a row written under a different Security:SecretKey does
            // not decrypt to nonsense — it throws. Left to reach the pipeline it is a 500 whose
            // ProblemDetails title is the framework's own "An error occurred while processing
            // your request.", which is what the Integrations page rendered: an admin screen
            // reporting a nameable, operator-fixable condition as an unexplained fault. Named
            // here instead, so it arrives as the same reported failure every other admin slice
            // sends — a sentence and a code — rather than as a trip to the logs.
            throw DomainException.Invalid(
                CredentialUnreadableCode,
                $"The stored {provider} credential cannot be read: it was encrypted with a "
                + "different Security:SecretKey than this deployment is configured with. "
                + $"Connecting {provider} again replaces it.");
        }

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

        // A connect or a reconnect is the operator asserting that this credential works, and it is
        // the only thing that clears a fault by hand. All three move together: a state cleared
        // with a stale error beside it is one screen reading the state and another reading the
        // error, and they disagree.
        row.Health = CredentialHealth.Healthy;
        row.LastError = null;
        row.LastSuccessAt = now;

        if (accountLabel is not null)
        {
            row.AccountLabel = accountLabel;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Records that the provider answered. Writes only when something is actually different — the
    /// state was not healthy, the credential has never succeeded, or the last success has gone
    /// stale past <see cref="SuccessRefreshSeconds"/>.
    /// </summary>
    /// <remarks>
    /// Uncancellable, like <see cref="MarkUnhealthyAsync"/>: a recovery discovered on a request
    /// the caller then abandoned is still a recovery, and losing it leaves every admin screen
    /// reporting a fault that has already cleared.
    /// </remarks>
    public async Task MarkHealthyAsync(IntegrationProvider provider, CancellationToken cancellationToken)
    {
        var row = await FindAsync(provider, cancellationToken);
        if (row is null)
        {
            return;
        }

        var now = clock.GetCurrentInstant().ToUnixTimeSeconds();
        var stale = (row.LastSuccessAt is null) || ((now - row.LastSuccessAt.Value) >= SuccessRefreshSeconds);

        if ((row.Health == CredentialHealth.Healthy) && (row.LastError is null) && !stale)
        {
            return;
        }

        row.Health = CredentialHealth.Healthy;
        row.LastError = null;
        row.LastSuccessAt = now;
        row.UpdatedAt = now;

        await context.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// Records that the provider refused, with a code a screen can turn into a sentence.
    /// <see cref="IntegrationCredential.LastSuccessAt"/> is left alone: the failure does not
    /// un-happen the last success, and that timestamp is the half of the alert an operator can act
    /// on.
    /// </summary>
    /// <remarks>
    /// Uncancellable by design — it runs while a refresh is failing, and the request that
    /// triggered it may already be going away. Losing this write leaves every screen saying
    /// everything is fine about a credential that can never work again.
    /// </remarks>
    public async Task MarkUnhealthyAsync(
        IntegrationProvider provider,
        CredentialHealth health,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var row = await FindAsync(provider, cancellationToken);
        if (row is null)
        {
            return;
        }

        row.Health = health;
        row.LastError = errorCode;
        row.UpdatedAt = clock.GetCurrentInstant().ToUnixTimeSeconds();

        await context.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// Whether this deployment's key can open the provider's stored credential, answered without
    /// knowing what is inside it. Returns for a provider that was never connected: absent is not a
    /// fault.
    /// </summary>
    /// <remarks>
    /// It exists because the Integrations page used to learn this by decrypting through a
    /// connector method that has since been deleted — the diagnosis was a side effect of a call
    /// that said something else, and would have vanished with it silently, since a page that
    /// renders is a page that looks fine.
    /// </remarks>
    public async Task EnsureReadableAsync(IntegrationProvider provider, CancellationToken cancellationToken)
    {
        var row = await FindAsync(provider, cancellationToken);
        if (row is null)
        {
            return;
        }

        try
        {
            protector.Unprotect(row.ProtectedPayload);
        }
        catch (Exception exception)
            when (exception is CryptographicException
                or FormatException
                or ArgumentException
                or OverflowException)
        {
            throw DomainException.Invalid(
                CredentialUnreadableCode,
                $"The stored {provider} credential cannot be read: it was encrypted with a "
                + "different Security:SecretKey than this deployment is configured with. "
                + $"Connecting {provider} again replaces it.");
        }
    }

    /// <summary>Tracked: the write path mutates what this returns.</summary>
    private Task<IntegrationCredential?> FindAsync(
        IntegrationProvider provider,
        CancellationToken cancellationToken) =>
        context.IntegrationCredentials.FirstOrDefaultAsync(
            row => row.Provider == provider, cancellationToken);
}
