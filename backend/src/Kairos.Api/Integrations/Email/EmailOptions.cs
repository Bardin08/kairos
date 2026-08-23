namespace Kairos.Api.Integrations.Email;

/// <remarks>
/// No <c>Enabled</c> flag: an absent <see cref="Host"/> already says off, as an absent
/// <see cref="Webhooks.WebhookOptions.Url"/> does. Only <see cref="Password"/> is a secret — a
/// hostname, port, username and sending address are reviewable configuration and belong in git,
/// the way the operator's identity stack already splits the same relay. The password arrives from
/// the environment and never reaches a row, so <see cref="Kairos.Api.Security.SecretProtector"/>,
/// which keeps credentials out of the replicated SQLite file, has nothing to protect here.
/// </remarks>
public sealed class EmailOptions
{
    /// <summary>In the environment, <c>Email__…</c>.</summary>
    public const string SectionName = "Email";

    /// <summary>
    /// RFC 6409 submission. This is the port STARTTLS lives on; 465 is implicit TLS from the
    /// first byte and <see cref="EmailSender"/> does not speak it.
    /// </summary>
    private const int SubmissionPort = 587;

    /// <summary>Null or empty means no relay, which means no mail and no failure.</summary>
    public string? Host { get; set; }

    public int Port { get; set; } = SubmissionPort;

    /// <summary>Blank skips authentication outright, for a relay that accepts by network instead.</summary>
    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>
    /// Also the .ics ORGANIZER, after parsing. Accepts a bare address or a display name around
    /// one, so naming the sender costs configuration rather than code.
    /// </summary>
    public string? From { get; set; }

    /// <summary>
    /// The origin the manage link is built on, without a trailing path.
    /// </summary>
    /// <remarks>
    /// Not the relay's setting, but the confirmation is the only thing in Kairos that names this
    /// instance from outside a request — every other URL is relative to the browser that asked. It
    /// moves to its own section the day a second reader appears. Required rather than optional: a
    /// confirmation whose only route to cancelling is a relative path is worse than none, because
    /// the invitee cannot tell it is broken.
    /// </remarks>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Read once per booking, and once at startup so the answer is in the log rather than inferred
    /// from mail that never arrives.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host)
        && !string.IsNullOrWhiteSpace(From)
        && !string.IsNullOrWhiteSpace(PublicBaseUrl);
}
