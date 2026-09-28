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
///
/// The other direction of that tradeoff is real and unhandled here: this row has no foreign key to
/// the credential and no cascade, so reconnecting under a different account leaves the saved ids
/// pointing at the previous account's calendars. Clearing them on that transition is its own
/// change, with its own test.
/// </remarks>
public sealed class IntegrationSetting
{
    public Guid Id { get; set; }

    public IntegrationProvider Provider { get; set; }

    /// <summary>
    /// A JSON object keyed by field key, with one key it never holds:
    /// <see cref="Integrations.IIntegrationSettings.EnabledKey"/> is reserved, because the switch
    /// is <see cref="IntegrationCredential.Enabled"/> and not a described field. JSON for the
    /// reason <see cref="IntegrationCredential.ProtectedPayload"/> gives and
    /// <see cref="BookingParticipant"/> rejects: never searched, never joined, never constrained by
    /// a foreign key. Plaintext, because none of it is a secret — a calendar id is an address, and
    /// one is already stored in the clear as an account label.
    /// </summary>
    public required string FieldValues { get; set; }

    /// <summary>
    /// Stamped when the row is written, and read by nothing — the state
    /// <see cref="IntegrationCredential.UpdatedAt"/> is also in. Kept rather than dropped because
    /// the moment it is wanted is a settings bug being diagnosed, and a row nobody dated then is a
    /// row nobody can date afterwards.
    /// </summary>
    public long UpdatedAt { get; set; }
}
