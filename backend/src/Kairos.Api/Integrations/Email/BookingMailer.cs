using System.Text;

using Kairos.Api.Persistence.Entities;

using Microsoft.Extensions.Options;

using MimeKit;
using MimeKit.Text;
using MimeKit.Utils;

using NodaTime;
using NodaTime.Text;

namespace Kairos.Api.Integrations.Email;

/// <summary>
/// The confirmation an invitee gets: what was booked, when it is, the link that lets them undo
/// it, and the .ics that makes their client offer accept and decline.
/// </summary>
/// <remarks>
/// Composition is separated from sending because composition is the half that can be proved. The
/// MIME structure, the agreement between the .ics METHOD and the MIME <c>method=</c> parameter,
/// and the manage link are all assertable without a relay; whether the message lands in an inbox
/// is not assertable at all and is verified by sending real mail to real accounts.
/// </remarks>
public sealed class BookingMailer(
    EmailSender sender,
    IcsBuilder calendar,
    IOptions<EmailOptions> options,
    ILogger<BookingMailer> logger)
{
    /// <summary>The route the frontend claims: <c>/manage/:token</c>, keyed on the token alone.</summary>
    private const string ManagePath = "manage";

    private const string CalendarSubtype = "calendar";
    private const string AttachmentMediaType = "application";
    private const string AttachmentSubtype = "ics";
    private const string AttachmentFileName = "invite.ics";
    private const string MethodParameter = "method";
    private const string AlternativeSubtype = "alternative";
    private const string MixedSubtype = "mixed";

    private static readonly ZonedDateTimePattern DatePattern =
        ZonedDateTimePattern.CreateWithInvariantCulture("dddd d MMMM yyyy", DateTimeZoneProviders.Tzdb);

    private static readonly ZonedDateTimePattern TimePattern =
        ZonedDateTimePattern.CreateWithInvariantCulture("HH:mm", DateTimeZoneProviders.Tzdb);

    /// <summary>
    /// Sends the confirmation, or does not, and either way returns.
    /// </summary>
    /// <remarks>
    /// Ground rule 14, at the only place it can be enforced: the booking is committed before this
    /// is called, and nothing in here can reach back and undo it. Both ways out are silent to the
    /// caller — no relay configured is the ordinary state of a fresh deployment, and a relay that
    /// refused is a delivery problem rather than a booking problem. A booking that answered 500
    /// because SMTP was down would cost the organizer every meeting rather than one notification.
    ///
    /// What that trades away, said plainly: a failed send is not retried and the invitee gets
    /// nothing. It is logged at Warning with the booking id so the organizer can resend by hand.
    /// The outbox would give retries for free and is the right home for the webhook, but not for
    /// this: an email row would have to carry the raw manage token in its payload, and Part A
    /// stores only that token's SHA-256 on purpose. Persisting the plaintext to buy a retry would
    /// make one database read worth cancel-and-reschedule on every booking in the file.
    /// </remarks>
    public async Task SendConfirmationAsync(
        Booking booking,
        string manageToken,
        CancellationToken cancellationToken)
    {
        if (!options.Value.IsConfigured)
        {
            logger.LogDebug(
                "{Section} is not configured; booking {Booking} was written without a confirmation.",
                EmailOptions.SectionName,
                booking.Id);
            return;
        }

        try
        {
            await sender.SendAsync(Compose(booking, manageToken), cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                exception,
                "Could not send the confirmation for booking {Booking}. The booking stands and the "
                + "invitee has not been told; there is no retry, so resend by hand if it matters.",
                booking.Id);
        }
    }

    /// <summary>
    /// The message, exactly as it goes on the wire.
    /// </summary>
    /// <remarks>
    /// The structure is Google Calendar's own, because Gmail and Outlook are the two clients that
    /// have to render an accept/decline control and that shape is the one both are known to
    /// handle:
    /// <code>
    /// multipart/mixed
    /// ├── multipart/alternative
    /// │   ├── text/plain                          the body, carrying the manage link
    /// │   └── text/calendar; method=REQUEST       what draws the accept/decline control
    /// └── application/ics (attachment)            what clients that ignore the part above save
    /// </code>
    /// The calendar part inside the alternative is what Gmail and Outlook.com read; the
    /// attachment is what Apple Mail and desktop clients act on. Both carry the same bytes.
    /// </remarks>
    public MimeMessage Compose(Booking booking, string manageToken)
    {
        var settings = options.Value;
        var booker = booking.Participants.First(participant => participant.Role == ParticipantRole.Booker);
        var organizer = MailboxAddress.Parse(settings.From!);
        var manageUrl = ManageUrl(settings.PublicBaseUrl!, manageToken);

        var start = Instant.FromUnixTimeSeconds(booking.StartUtc);
        var end = Instant.FromUnixTimeSeconds(booking.EndUtc);

        // The booker's zone, not the organizer's. The times in this mail are read by the person
        // it is addressed to, and they told us which zone they read them in.
        var zone = DateTimeZoneProviders.Tzdb[booker.TimeZoneId];

        // One value, two readers. The .ics METHOD and the MIME method= parameter are derived from
        // this single constant rather than written out twice, because a message labelled one way
        // around a body labelled the other is discarded by clients without a word to either side.
        const IcsMethod method = IcsMethod.Request;

        var ics = calendar.Build(new IcsRequest(
            BookingId: booking.Id,
            Start: start,
            End: end,
            Summary: booking.Link.Name,
            Description: $"Manage or cancel: {manageUrl}",
            OrganizerEmail: organizer.Address,
            AttendeeEmail: booker.Email,
            Sequence: booking.Sequence,
            Method: method));

        var body = new TextPart(TextFormat.Plain);
        body.SetText(Encoding.UTF8, Body(booker.Name, booking.Link.Name, start, end, zone, manageUrl));

        var invitation = new TextPart(CalendarSubtype)
        {
            // Base64 rather than left to MimeKit's choice. The .ics is folded at 75 octets with a
            // single leading space marking each continuation, and any encoding that normalises
            // whitespace or line endings unfolds it into a file no client will parse. Base64 is
            // the one encoding that cannot touch the bytes.
            ContentTransferEncoding = ContentEncoding.Base64,
        };
        invitation.ContentType.Parameters[MethodParameter] = IcsBuilder.Keyword(method);
        invitation.SetText(Encoding.UTF8, ics);

        var attachment = new MimePart(AttachmentMediaType, AttachmentSubtype)
        {
            Content = new MimeContent(new MemoryStream(Encoding.UTF8.GetBytes(ics))),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = AttachmentFileName,
        };

        var message = new MimeMessage
        {
            Subject = Subject(booking.Link.Name, start, zone),
            Body = new Multipart(MixedSubtype)
            {
                new Multipart(AlternativeSubtype) { body, invitation },
                attachment,
            },
        };

        message.From.Add(organizer);
        message.To.Add(new MailboxAddress(booker.Name, booker.Email));

        // MimeKit otherwise derives the Message-Id's right-hand side from the machine's hostname,
        // which in a container is a random hex string and not a resolvable domain. Filters read
        // that as a forgery signal, and this whole task is about landing in an inbox. The sending
        // domain is the honest answer and costs one line.
        message.MessageId = MimeUtils.GenerateMessageId(organizer.Domain);

        return message;
    }

    /// <summary>
    /// The invitee's only route to cancelling or moving the meeting, and the shape Part A's
    /// frontend routes: the raw token in the path, nothing else. Escaped and the base trimmed,
    /// so a base URL written with a trailing slash does not produce a double one that some mail
    /// clients decline to linkify.
    /// </summary>
    private static string ManageUrl(string baseUrl, string manageToken) =>
        $"{baseUrl.TrimEnd('/')}/{ManagePath}/{Uri.EscapeDataString(manageToken)}";

    private static string Subject(string linkName, Instant start, DateTimeZone zone)
    {
        var local = start.InZone(zone);
        return $"Confirmed: {linkName}, {DatePattern.Format(local)} at {TimePattern.Format(local)} ({zone.Id})";
    }

    /// <summary>
    /// Plain text, and no HTML alternative. There is nothing here that needs a font, and a
    /// text-only body is one fewer thing for a filter to score.
    /// </summary>
    private static string Body(
        string bookerName,
        string linkName,
        Instant start,
        Instant end,
        DateTimeZone zone,
        string manageUrl) =>
        $"""
        Hi {bookerName},

        {linkName} is confirmed.

        {DatePattern.Format(start.InZone(zone))}
        {TimePattern.Format(start.InZone(zone))}-{TimePattern.Format(end.InZone(zone))} ({zone.Id})

        To move or cancel this booking, open:
        {manageUrl}

        The attached invitation adds it to your calendar.
        """;
}
