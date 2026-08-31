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
/// <see cref="WriteAsync{TPayload}"/> and nowhere else: never a field, never a response, never a
/// log argument. What crosses the boundary is a typed payload the provider defined.
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
    /// What a provider nobody has connected is called, in the one place both refusals of it can
    /// read from. It lives on the store rather than beside either caller because the absent thing
    /// is this table's row, and the two spellings it replaced — "Integration" here and a sentence
    /// of its own in
    /// <see cref="Features.Integrations.IntegrationProviderResolver.RequireConnectedAsync"/> — were
    /// one condition answering to two names, with <c>not_found</c> on both and nothing else for an
    /// operator reading a log to tell them apart by.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="Features.Integrations.IntegrationProviderResolver.UnknownProviderName"/>.
    /// A provider this build cannot name and a provider nobody has connected are different things
    /// to do about, so they keep different titles.
    /// </remarks>
    public const string MissingConnectionName = "A connection to that integration";

    /// <summary>
    /// How stale a proven success may get before a fresh one is worth a write. The success path
    /// runs on every token refresh, and a refresh runs on every booking page load, so an
    /// unthrottled stamp is a SQLite write per page view. Five minutes is finer than any screen.
    /// </summary>
    private const int SuccessRefreshSeconds = 300;

    /// <summary>
    /// The rows carry ciphertext and the caller has no way to read it, which is why handing back
    /// the entity is safe here.
    /// </summary>
    public async Task<IReadOnlyList<IntegrationCredential>> ListAsync(CancellationToken cancellationToken) =>
        await context.IntegrationCredentials.AsNoTracking().ToListAsync(cancellationToken);

    /// <summary>
    /// Null for either of the two states in which there is nothing to talk to the provider with:
    /// never connected, or connected and switched off. Neither is an error.
    /// </summary>
    public async Task<TPayload?> ReadAsync<TPayload>(
        IntegrationProvider provider,
        CancellationToken cancellationToken)
        where TPayload : class
    {
        var row = await FindAsync(provider, cancellationToken);

        // Two states, one answer. Never connected and switched off are the same fact to a caller:
        // there is nothing here to talk to the provider with.
        //
        // The gate is not on its own enough to pause a provider, and believing it was is what
        // shipped a switch that did nothing on a running instance: an adapter that caches what it
        // read never asks again, so it never learns. GoogleClientFactory caches the credential and
        // checks IsPausedAsync ahead of that cache for exactly this reason. What this line still
        // buys is the cold path and the provider added later, which is a floor rather than the
        // whole switch.
        if (row is null || !row.Enabled)
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
            // not decrypt to nonsense — it throws. Left to the pipeline it becomes a 500 titled
            // with the framework's "An error occurred while processing your request.", which is
            // what the Integrations page rendered: an operator-fixable condition as an unexplained
            // fault. Named here so it arrives as a sentence and a code, like every other admin
            // slice.
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

        // A connect or a reconnect is the operator asserting the credential works, and the only
        // thing that clears a fault by hand. All three move together: a state cleared with a stale
        // error beside it is two screens disagreeing.
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
    /// Whether the operator has switched this provider off. True only where there is a row and its
    /// switch is off: a provider nobody connected is not paused, and holding those two apart is
    /// what keeps a disconnect a fault while a pause is not one.
    /// </summary>
    /// <remarks>
    /// The switch, asked without opening the credential — for callers that have to know before
    /// reaching for one, or that hold a credential already and would otherwise never ask again.
    /// One indexed row by <see cref="IntegrationProvider"/>, and every caller follows it with an
    /// HTTP round trip it is saving.
    /// </remarks>
    public Task<bool> IsPausedAsync(IntegrationProvider provider, CancellationToken cancellationToken) =>
        context.IntegrationCredentials.AsNoTracking()
            .AnyAsync(row => (row.Provider == provider) && !row.Enabled, cancellationToken);

    /// <summary>
    /// Turns the provider on or off. A provider with no stored credential has no row to carry the
    /// switch, and there is nothing to pause about a connection nobody made, so this throws rather
    /// than inserting one.
    /// </summary>
    /// <remarks>
    /// No early return for a switch already in the position asked for, unlike
    /// <see cref="MarkHealthyAsync"/>. That one skips a write it measured — a refresh per booking
    /// page view — and this one is pressed by hand, so there is nothing to save and the skip would
    /// only cost <see cref="IntegrationCredential.UpdatedAt"/> its meaning: every other writer here
    /// stamps it, and a row that says when it was last touched has to be right about it.
    /// </remarks>
    public async Task SetEnabledAsync(
        IntegrationProvider provider,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var row = await FindAsync(provider, cancellationToken)
            ?? throw DomainException.NotFound(MissingConnectionName);

        row.Enabled = enabled;
        row.UpdatedAt = clock.GetCurrentInstant().ToUnixTimeSeconds();

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Records that the provider answered, writing only when something is actually different.
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
    /// un-happen the last success, and that timestamp is the half of the alert an operator acts on.
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
    /// connector method since deleted — the diagnosis was a side effect of a call that said
    /// something else, and would have vanished with it silently.
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
