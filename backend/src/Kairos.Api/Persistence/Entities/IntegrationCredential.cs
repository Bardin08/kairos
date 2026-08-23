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

    /// <summary>
    /// Whether this credential still works, written by whatever discovers that it does not.
    /// Google's adapter is the only writer today, and that is an accident of there being one
    /// provider — the column belongs to the credential, not to the adapter.
    /// </summary>
    /// <remarks>
    /// Initialised rather than left to default(CredentialHealth), which is 0 and is not a value of
    /// the enum. A row written by a path that never thought about health is healthy — the failure
    /// has to be discovered and recorded, never assumed.
    /// </remarks>
    public CredentialHealth Health { get; set; } = CredentialHealth.Healthy;

    /// <summary>
    /// Unix seconds of the last proven use, or null when there has not been one. This is what
    /// makes an alert actionable: "reconnect" without "it last worked in March" leaves the
    /// operator unable to tell a five-minute outage from a season of lost meetings.
    /// </summary>
    public long? LastSuccessAt { get; set; }

    /// <summary>
    /// Why it stopped, as a code and never as a provider's sentence — <c>invalid_grant</c>, not a
    /// paragraph from an OAuth server. Never anything derived from the payload: the only reader is
    /// a browser.
    /// </summary>
    public string? LastError { get; set; }

    public long ConnectedAt { get; set; }

    public long UpdatedAt { get; set; }
}
