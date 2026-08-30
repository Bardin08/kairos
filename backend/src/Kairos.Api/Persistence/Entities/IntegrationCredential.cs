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
    /// AES-GCM ciphertext of a small JSON document whose shape is the provider's business — a
    /// refresh token and a calendar id for Google. Encrypted whole, read whole, never queried by
    /// content.
    /// </summary>
    /// <remarks>
    /// JSON is right here and was wrong for <see cref="BookingParticipant"/>: a credential payload
    /// is never searched, joined or constrained by a foreign key, and it is a secret — a single
    /// opaque blob whichever way it is stored. The queryable parts stay columns.
    /// </remarks>
    public required string ProtectedPayload { get; set; }

    /// <summary>Which account this is, for the admin UI. Never a secret — an email or an id.</summary>
    public string? AccountLabel { get; set; }

    /// <summary>
    /// Written by whatever discovers the credential has stopped working. Google's adapter is the
    /// only writer today, an accident of there being one provider: the column belongs to the
    /// credential.
    /// </summary>
    /// <remarks>
    /// Initialised rather than left to default(CredentialHealth), which is 0 and not a value of the
    /// enum. A row written by a path that never thought about health is healthy — failure has to be
    /// discovered, never assumed.
    /// </remarks>
    public CredentialHealth Health { get; set; } = CredentialHealth.Healthy;

    /// <summary>
    /// The operator's switch. Off, the credential is kept and nothing is read or written with it —
    /// which is not the same as disconnected, where the grant is revoked and the row is gone.
    /// </summary>
    /// <remarks>
    /// Initialised true rather than left to <c>default(bool)</c>, which is false: a row written by
    /// a path that never thought about the switch is a working integration, and an upgrade that
    /// silently paused every connection would be the worst possible reading of this column. The
    /// migration gives existing rows the same default.
    ///
    /// A column and not a described setting, because the generic layer queries it — the reason
    /// this class already gives for keeping the queryable parts as columns.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Unix seconds of the last proven use, or null when there has not been one. It is what makes
    /// an alert actionable: "reconnect" without "it last worked in March" leaves the operator
    /// unable to tell a five-minute outage from a season of lost meetings.
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
