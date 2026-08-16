namespace Kairos.Api.Integrations.Email;

/// <summary>
/// The relay a confirmation goes through, and the address it comes from.
/// </summary>
/// <remarks>
/// Six settings because six have a reader, and no <c>Enabled</c> flag: an absent
/// <see cref="Host"/> already says off, the same way an absent
/// <see cref="Webhooks.WebhookOptions.Url"/> does. Four of these are deliberately not secrets —
/// a hostname, a port, a username and a sending address are reviewable configuration and belong
/// in git. <see cref="Password"/> is the only one that has to arrive from a secret store, which
/// is how the operator's identity stack already splits the same relay.
///
/// It is also configuration all the way down: the password is read from the environment and
/// never written to a row, so <see cref="Kairos.Api.Security.SecretProtector"/> — which exists
/// to keep credentials out of the replicated SQLite file — has nothing to protect here.
/// </remarks>
public sealed class EmailOptions
{
    /// <summary>Binds <c>Email</c>; in the environment, <c>Email__…</c>.</summary>
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

    /// <summary>
    /// The only value here that is a secret. It reaches the process from the environment and
    /// stops there.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Who the mail is from, and — after parsing — the .ics ORGANIZER. Accepts a bare address or
    /// a display name around one, so naming the sender costs configuration rather than code.
    /// </summary>
    public string? From { get; set; }

    /// <summary>
    /// The origin the manage link is built on, without a trailing path.
    /// </summary>
    /// <remarks>
    /// The one setting in this class that is not the relay's. It lives here because the
    /// confirmation is the only thing in Kairos that has to name this instance from outside a
    /// request — every other URL is relative to the browser that asked. It moves to a section of
    /// its own the day a second reader appears, and not before.
    ///
    /// Required rather than optional: the body's whole job is to carry the manage link, and a
    /// confirmation whose only route to cancelling is a relative path is worse than no
    /// confirmation, because the invitee has no way to tell it is broken.
    /// </remarks>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Whether there is enough here to send at all. Read once per booking, and once at startup so
    /// the answer is in the log rather than inferred from mail that never arrives.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host)
        && !string.IsNullOrWhiteSpace(From)
        && !string.IsNullOrWhiteSpace(PublicBaseUrl);
}
