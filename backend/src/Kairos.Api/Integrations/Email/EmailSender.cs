using MailKit.Net.Smtp;
using MailKit.Security;

using Microsoft.Extensions.Options;

using MimeKit;

namespace Kairos.Api.Integrations.Email;

/// <remarks>
/// The thinnest type in the feature. Everything decidable without a socket is
/// <see cref="BookingMailer"/>'s and is tested there; what is left is a connect, an authenticate
/// and a send, and a mock of SMTP would test MailKit rather than Kairos. So this file has no
/// tests and stays small enough not to need any.
///
/// A connection per message rather than a pooled one: this sends a handful of messages a day,
/// and a client held open across hours has to notice the relay dropped it.
///
/// Open and <see cref="SendAsync"/> virtual all the same: what is refused is a mock standing in
/// for this class in a test OF this class, not a substitute in a test of a caller that acts on
/// the answer — the propose route, which gives a held slot back when the relay refuses.
/// </remarks>
public class EmailSender(IOptions<EmailOptions> options)
{
    /// <summary>
    /// Bounded because this runs on the booking's own request thread: long enough for greylisting
    /// or a slow TLS handshake, short enough that a black hole costs a pause rather than a browser
    /// timeout on a booking already written.
    /// </summary>
    /// <remarks>
    /// Public because it is the cost of one send against a relay that never answers, and a caller
    /// on a schedule has to budget against it rather than guess —
    /// <see cref="Features.Bookings.ProposalSweeper"/> sizes its batch on this and on nothing else.
    /// </remarks>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);

    /// <remarks>
    /// The response is the only handle tying a booking to something the operator can find at the
    /// relay. Twice during this feature's life a message Kairos had not sent was read as proof
    /// that Kairos could send; a queue id in the log settles that.
    /// </remarks>
    public virtual async Task<string> SendAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        var relay = options.Value;

        // Not a second configuration check — <see cref="EmailOptions.IsConfigured"/> answers that,
        // and answering twice is how the two drift apart. This asserts the caller asked: no host
        // here is a wiring mistake that should name itself, not MailKit's ArgumentNullException.
        var host = relay.Host ?? throw new InvalidOperationException(
            $"{EmailOptions.SectionName}:Host is not configured; nothing should be calling this.");

        using var client = new SmtpClient
        {
            Timeout = (int)SendTimeout.TotalMilliseconds,

            // MailKit defaults this to true and treats a revocation check it could not *complete*
            // as fatal — not a revoked certificate, an unanswered question about one. Against
            // Gmail that question can never be answered: Google Trust Services stopped putting an
            // OCSP responder in its leaf certificates, so smtp.gmail.com publishes revocation by
            // CRL only. Measured 2026-08-15 against the live relay: zero OCSP URIs in the
            // presented chain, 12 of 12 connections refused with "an incomplete certificate
            // revocation check occurred", 12 of 12 succeeded with this line, and the CRL the
            // certificate does name fetched in 146ms.
            //
            // The trade: hostname, expiry, signature and trust anchor are still validated — this
            // is not a callback that accepts anything — but revocation checking is given up, so a
            // revoked, unexpired certificate an attacker could present would be accepted. Without
            // the line every confirmation fails, always, the cause being an absent OCSP endpoint
            // rather than an unreachable one: between shipping and this fix Kairos sent no mail
            // while every booking succeeded and logged a warning blaming TLS.
            CheckCertificateRevocation = false,
        };

        // StartTls, not StartTlsWhenAvailable and not Auto: the permissive options fall back to an
        // unencrypted session when the relay does not advertise STARTTLS, putting the relay
        // password on the wire in the clear. A relay that cannot do STARTTLS is refused loudly.
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
