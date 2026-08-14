namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// One row per connected provider. Not one table per provider: GoogleCredential,
/// MicrosoftCredential and ZoomCredential would be identical shapes with three configurations,
/// three stores and a migration each — the same split the CalendarEntry/ConferenceLink pair
/// already rejected.
/// </summary>
public sealed class IntegrationCredential
{
    public Guid Id { get; set; }

    public IntegrationProvider Provider { get; set; }

    /// <summary>
    /// AES-GCM ciphertext of a small JSON document whose shape is the provider's business:
    /// Google stores a refresh token and a calendar id, Zoom would store a refresh token and
    /// an account id. Encrypted as a whole, read as a whole, never queried by content.
    /// </summary>
    /// <remarks>
    /// JSON is right here and was wrong for <see cref="BookingParticipant"/> because the reasons
    /// that killed it there do not apply: a credential payload is never searched, never joined,
    /// never constrained by a foreign key, and it is a secret — so it is a single opaque blob
    /// whichever way it is stored. The queryable parts stay columns.
    /// </remarks>
    public required string ProtectedPayload { get; set; }

    /// <summary>Which account this is, for the admin UI. Never a secret — an email or an id.</summary>
    public string? AccountLabel { get; set; }

    public long ConnectedAt { get; set; }

    public long UpdatedAt { get; set; }
}
