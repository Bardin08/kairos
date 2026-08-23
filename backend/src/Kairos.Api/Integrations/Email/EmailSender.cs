using MailKit.Net.Smtp;
using MailKit.Security;

using Microsoft.Extensions.Options;

using MimeKit;

namespace Kairos.Api.Integrations.Email;

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
///
/// Open and <see cref="SendAsync"/> virtual, which does not contradict the paragraph above. What
/// is refused is a mock standing in for this class in a test OF this class; what is allowed is a
/// substitute in a test of a caller that has to do something with the answer. The propose route
/// is one: it gives a held slot back when the relay refuses, and reaching that branch through a
/// real socket would mean a test that waits out a connect timeout to assert a compensation.
/// </remarks>
public class EmailSender(IOptions<EmailOptions> options)
{
    /// <summary>
    /// Bounded because this runs on the booking's own request thread. Long enough that a relay
    /// doing greylisting or a slow TLS handshake still succeeds, short enough that a black hole
    /// costs the booker a pause rather than a browser timeout on a booking that was already
    /// written.
    /// </summary>
    /// <remarks>
    /// Public because it is the cost of one send against a relay that never answers, and a caller
    /// on a schedule has to budget against it rather than guess —
    /// <see cref="Features.Bookings.ProposalSweeper"/> sizes its batch on this and on nothing else.
    /// </remarks>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);

    /// <remarks>
    /// The response is returned rather than discarded because it is the only handle that ties a
    /// booking to something the operator can find at the relay. Twice during this feature's life
    /// a message that Kairos had not sent was read as proof that Kairos could send, because
    /// nothing on this path said otherwise; a queue id in the log is what makes that question
    /// answerable instead of arguable.
    /// </remarks>
    public virtual async Task<string> SendAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        var relay = options.Value;

        // Not a second configuration check — <see cref="EmailOptions.IsConfigured"/> already
        // answers that question and answering it twice is how the two answers drift apart. This
        // is the assertion that the caller asked it: arriving here with no host is a wiring
        // mistake, and it should name itself rather than surface as MailKit's ArgumentNullException.
        var host = relay.Host ?? throw new InvalidOperationException(
            $"{EmailOptions.SectionName}:Host is not configured; nothing should be calling this.");

        using var client = new SmtpClient
        {
            Timeout = (int)SendTimeout.TotalMilliseconds,

            // MailKit defaults this to true and treats a revocation check it could not *complete*
            // as fatal — not a revoked certificate, an unanswered question about one. Against
            // Gmail that question can never be answered: Google Trust Services stopped putting an
            // OCSP responder in its leaf certificates, so the chain smtp.gmail.com presents
            // carries no OCSP URI at all and publishes revocation by CRL only. Measured on
            // 2026-08-15 against the live relay: zero OCSP URIs in the presented chain, and 12 of
            // 12 connections refused with "an incomplete certificate revocation check occurred"
            // while 12 of 12 succeeded with this line. The network was not the problem — the CRL
            // the certificate does name fetched in 146ms.
            //
            // The trade, stated plainly. The full chain is still validated: hostname, expiry,
            // signature and trust anchor all hold, and this is emphatically not a callback that
            // accepts anything. What is given up is revocation checking itself, so a certificate
            // that has been revoked but not yet expired would now be accepted if an attacker could
            // also present it. Against that: without this line every confirmation fails, always
            // — not on some networks and not intermittently, because the cause is the absence of
            // an OCSP endpoint rather than a network that cannot reach one. Between this feature
            // shipping and this fix, Kairos sent no email at all, while each booking succeeded and
            // logged a warning that blamed TLS.
            CheckCertificateRevocation = false,
        };

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

        var response = await client.SendAsync(message, cancellationToken);

        // QUIT rather than dropping the socket. A relay that never sees QUIT logs the session as
        // aborted, and enough aborted sessions is a reputation signal on a shared relay.
        await client.DisconnectAsync(true, cancellationToken);

        return response;
    }
}
