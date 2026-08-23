namespace Kairos.Api.Integrations.Webhooks;

/// <summary>
/// No Enabled flag: an absent <see cref="Url"/> already says off, and two ways to switch one
/// thing off is one way too many to get wrong.
/// </summary>
public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";

    /// <summary>
    /// A delivery is slow far more often than it is broken, so the bound is generous enough that
    /// a receiver doing real work still succeeds, and short enough that a black hole does not
    /// hold the dispatcher for a whole poll interval.
    /// </summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A receiver that has refused five times is down, not busy. Past that the row would be
    /// retried until the endpoint came back or the disk filled, and the failure would never
    /// become visible in <see cref="Persistence.Entities.OutboxMessage.LastError"/>.
    /// </summary>
    private const int DefaultMaxAttempts = 5;

    /// <summary>Null or empty means nothing is subscribed and the dispatcher has no work.</summary>
    public string? Url { get; set; }

    /// <summary>
    /// The HMAC key shared with the receiver. Configuration, not stored credential: it arrives
    /// from the environment and never reaches the database, so SecretProtector — which exists to
    /// keep secrets out of the replicated SQLite file — has nothing to protect here.
    /// </summary>
    public string? Secret { get; set; }

    public TimeSpan Timeout { get; set; } = DefaultTimeout;

    public int MaxAttempts { get; set; } = DefaultMaxAttempts;
}
