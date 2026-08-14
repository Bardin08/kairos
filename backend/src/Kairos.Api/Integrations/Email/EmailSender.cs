using MailKit.Net.Smtp;
using MailKit.Security;

using Microsoft.Extensions.Options;

using MimeKit;

namespace Kairos.Api.Integrations.Email;

/// <summary>
/// One message, over one connection, to the configured relay.
/// </summary>
/// <remarks>
/// Deliberately the thinnest type in the feature. Everything that can be decided without a socket
/// — what the message says, what it carries and whether to send at all — is
/// <see cref="BookingMailer"/>'s, and is tested there. What is left is a connect, an
/// authenticate and a send, which no test can assert about without either a relay or a mock of
/// the protocol, and a mock of SMTP tests MailKit rather than Kairos. So this file has no tests
/// and is kept small enough that it does not need any: the only evidence it works is mail
/// arriving in an inbox.
///
/// A connection per message rather than a pooled one. A self-hosted single-organizer scheduler
/// sends a handful of messages a day, and a client held open across hours is a client that has
/// to notice the relay dropped it.
/// </remarks>
public sealed class EmailSender(IOptions<EmailOptions> options)
{
    /// <summary>
    /// Bounded because this runs on the booking's own request thread. Long enough that a relay
    /// doing greylisting or a slow TLS handshake still succeeds, short enough that a black hole
    /// costs the booker a pause rather than a browser timeout on a booking that was already
    /// written.
    /// </summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);

    public async Task SendAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        var relay = options.Value;

        // Not a second configuration check — <see cref="EmailOptions.IsConfigured"/> already
        // answers that question and answering it twice is how the two answers drift apart. This
        // is the assertion that the caller asked it: arriving here with no host is a wiring
        // mistake, and it should name itself rather than surface as MailKit's ArgumentNullException.
        var host = relay.Host ?? throw new InvalidOperationException(
            $"{EmailOptions.SectionName}:Host is not configured; nothing should be calling this.");

        using var client = new SmtpClient { Timeout = (int)SendTimeout.TotalMilliseconds };

        // StartTls, not StartTlsWhenAvailable and not Auto. The permissive options fall back to
        // an unencrypted session when the relay does not advertise STARTTLS, and the fallback
        // puts the relay password on the wire in the clear. A relay that cannot do STARTTLS is
        // one this refuses to talk to, loudly, rather than one it quietly downgrades for.
        await client.ConnectAsync(host, relay.Port, SecureSocketOptions.StartTls, cancellationToken);

        // Skipped rather than attempted with a blank password: a relay that accepts by network
        // address answers AUTH with a 5xx, which would turn a working configuration into a
        // failure that reads like bad credentials.
        if (!string.IsNullOrWhiteSpace(relay.Username))
        {
            // An empty password rather than a refusal to try: the relay's own 535 names the
            // problem better than a guess from here, and a username with no password is the shape
            // a missing secret takes when the secret store has not synced yet.
            await client.AuthenticateAsync(
                relay.Username, relay.Password ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);

        // QUIT rather than dropping the socket. A relay that never sees QUIT logs the session as
        // aborted, and enough aborted sessions is a reputation signal on a shared relay.
        await client.DisconnectAsync(true, cancellationToken);
    }
}
