namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// One row per provider, holding whatever that provider's own settings are. Not one table per
/// provider, for the reason <see cref="IntegrationCredential"/> gives: identical shapes with a
/// configuration, a store and a migration each.
/// </summary>
/// <remarks>
/// Deliberately not folded into <see cref="IntegrationCredential.ProtectedPayload"/>. That blob is
/// replaced wholesale on every reconnect, so a calendar selection kept there would evaporate the
/// first time an operator fixed a revoked grant — the moment they can least afford a silent
/// change to what Kairos considers busy.
/// </remarks>
public sealed class IntegrationSetting
{
    public Guid Id { get; set; }

    public IntegrationProvider Provider { get; set; }

    /// <summary>
    /// A JSON object keyed by field key. JSON for the reason
    /// <see cref="IntegrationCredential.ProtectedPayload"/> gives and
    /// <see cref="BookingParticipant"/> rejects: never searched, never joined, never constrained by
    /// a foreign key. Plaintext, because none of it is a secret — a calendar id is an address, and
    /// one is already stored in the clear as an account label.
    /// </summary>
    public required string Values { get; set; }

    public long UpdatedAt { get; set; }
}
