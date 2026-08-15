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
/// What an invitee is told about their meeting: that it is booked, that it has moved, or that it
/// is off — each with the times, the link that lets them act, and the .ics that makes their
/// client add, update or remove the event.
/// </summary>
/// <remarks>
/// Composition is separated from sending because composition is the half that can be proved. The
/// MIME structure, the agreement between the .ics METHOD and the MIME <c>method=</c> parameter,
/// and the manage link are all assertable without a relay; whether the message lands in an inbox
/// is not assertable at all and is verified by sending real mail to real accounts.
///
/// One composer for all three notices rather than three. The MIME shape, the transfer encodings,
/// the Message-Id and the zone the times are rendered in are identical across them and are the
/// parts that are delicate; what actually differs is three strings and the METHOD. Three
/// composers would be three places for the folded .ics to get its encoding wrong.
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

    /// <summary>The booking exists. <c>METHOD:REQUEST</c>, and the invitee's first sight of it.</summary>
    public Task SendConfirmationAsync(
        Booking booking,
        string manageToken,
        CancellationToken cancellationToken) =>
        SendAsync(BookingNotice.Confirmation, booking, manageToken, cancellationToken);

    /// <summary>
    /// The booking moved. <c>METHOD:REQUEST</c> again, with the same <c>UID</c> and a
    /// <see cref="Booking.Sequence"/> the caller has already advanced — which is precisely what
    /// makes a client treat this as a change to the meeting it is holding rather than as a second
    /// invitation. A stale sequence is not an error anybody is told about: the client discards the
    /// update without a word, nobody reports a reschedule that did not arrive, and the invitee
    /// turns up at the old time.
    /// </summary>
    public Task SendRescheduleAsync(
        Booking booking,
        string manageToken,
        CancellationToken cancellationToken) =>
        SendAsync(BookingNotice.Reschedule, booking, manageToken, cancellationToken);

    /// <summary>
    /// The booking is off. <c>METHOD:CANCEL</c>, which <see cref="IcsBuilder"/> already pairs with
    /// <c>STATUS:CANCELLED</c> — both, because Outlook removes the event on the status and leaves
    /// one marked CONFIRMED in place however the message was labelled.
    /// </summary>
    public Task SendCancellationAsync(
        Booking booking,
        string manageToken,
        CancellationToken cancellationToken) =>
        SendAsync(BookingNotice.Cancellation, booking, manageToken, cancellationToken);

    /// <summary>
    /// Sends the notice, or does not, and either way returns.
    /// </summary>
    /// <remarks>
    /// Ground rule 14, at the only place it can be enforced: the booking has been written, moved
    /// or cancelled before this is called, and nothing in here can reach back and undo it. Both
    /// ways out are silent to the caller — no relay configured is the ordinary state of a fresh
    /// deployment, and a relay that refused is a delivery problem rather than a booking problem. A
    /// cancellation that answered 500 because SMTP was down would leave the invitee pressing the
    /// button again on a meeting that is already off.
    ///
    /// What that trades away, said plainly: a failed send is not retried and the invitee gets
    /// nothing. It is logged at Warning with the booking id so the organizer can resend by hand.
    /// The outbox would give retries for free and is the right home for the webhook, but not for
    /// this: an email row would have to carry the raw manage token in its payload, and Part A
    /// stores only that token's SHA-256 on purpose. Persisting the plaintext to buy a retry would
    /// make one database read worth cancel-and-reschedule on every booking in the file. That is
    /// why all three of these run on the request that caused them, where the plaintext is already
    /// in hand — from the 201 for a confirmation, and from the URL the invitee just opened for the
    /// other two.
    /// </remarks>
    private async Task SendAsync(
        BookingNotice notice,
        Booking booking,
        string manageToken,
        CancellationToken cancellationToken)
    {
        if (!options.Value.IsConfigured)
        {
            logger.LogDebug(
                "{Section} is not configured; booking {Booking} changed without a {Notice} being sent.",
                EmailOptions.SectionName,
                booking.Id,
                notice);
            return;
        }

        try
        {
            var message = Compose(booking, manageToken, notice);
            var response = await sender.SendAsync(message, cancellationToken);

            // Said at Information, not Debug. Silence on the happy path is what let a message
            // Kairos had not sent be read twice as proof that Kairos could send: the only visible
            // difference between a working relay and a broken one was mail that may or may not
            // have come from somewhere else. The Message-Id and the relay's own response are the
            // two handles that settle it from a log line alone, and the sequence is the third —
            // it is what decides whether a client acts on the message or drops it.
            logger.LogInformation(
                "{Notice} for booking {Booking} at sequence {Sequence} accepted by the relay as "
                + "{MessageId}: {Response}",
                notice,
                booking.Id,
                booking.Sequence,
                message.MessageId,
                response);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                exception,
                "Could not send the {Notice} for booking {Booking}. The booking stands as it is and "
                + "the invitee has not been told; there is no retry, so resend by hand if it matters.",
                notice,
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
    public MimeMessage Compose(Booking booking, string manageToken, BookingNotice notice)
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
        // this single expression rather than written out twice, because a message labelled one way
        // around a body labelled the other is discarded by clients without a word to either side.
        var method = Method(notice);

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
        body.SetText(
            Encoding.UTF8, Body(notice, booker.Name, booking.Link.Name, start, end, zone, manageUrl));

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
            Subject = Subject(notice, booking.Link.Name, start, zone),
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

    /// <summary>
    /// A confirmation and a reschedule are both REQUEST — the second is the first at a new time,
    /// which is the entire mechanism by which a calendar client updates in place instead of
    /// showing two meetings.
    /// </summary>
    private static IcsMethod Method(BookingNotice notice) =>
        notice == BookingNotice.Cancellation ? IcsMethod.Cancel : IcsMethod.Request;

    /// <summary>
    /// The times in the subject are always the booking's current ones, so a reschedule is
    /// readable as a reschedule from the subject line alone and a cancellation names the slot it
    /// is releasing.
    /// </summary>
    private static string Subject(BookingNotice notice, string linkName, Instant start, DateTimeZone zone)
    {
        var local = start.InZone(zone);
        var lead = notice switch
        {
            BookingNotice.Confirmation => "Confirmed",
            BookingNotice.Reschedule => "Moved",
            BookingNotice.Cancellation => "Cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(notice), notice, "Unknown booking notice."),
        };

        return $"{lead}: {linkName}, {DatePattern.Format(local)} at {TimePattern.Format(local)} ({zone.Id})";
    }

    /// <summary>
    /// Plain text, and no HTML alternative. There is nothing here that needs a font, and a
    /// text-only body is one fewer thing for a filter to score.
    /// </summary>
    /// <remarks>
    /// The manage link is in all three, the cancellation included. It costs nothing and it is the
    /// only way an invitee can check that a cancellation they are reading about is real, rather
    /// than taking the word of an email — which is the same reason the link exists at all.
    /// </remarks>
    private static string Body(
        BookingNotice notice,
        string bookerName,
        string linkName,
        Instant start,
        Instant end,
        DateTimeZone zone,
        string manageUrl)
    {
        var (headline, closing) = notice switch
        {
            BookingNotice.Confirmation =>
                ($"{linkName} is confirmed.", "The attached invitation adds it to your calendar."),
            BookingNotice.Reschedule =>
                ($"{linkName} has moved. It is now:",
                    "The attached invitation updates the one already in your calendar."),
            BookingNotice.Cancellation =>
                ($"{linkName} has been cancelled. It was:",
                    "The attached invitation removes it from your calendar."),
            _ => throw new ArgumentOutOfRangeException(nameof(notice), notice, "Unknown booking notice."),
        };

        return $"""
            Hi {bookerName},

            {headline}

            {DatePattern.Format(start.InZone(zone))}
            {TimePattern.Format(start.InZone(zone))}-{TimePattern.Format(end.InZone(zone))} ({zone.Id})

            {Action(notice)}
            {manageUrl}

            {closing}
            """;
    }

    private static string Action(BookingNotice notice) =>
        notice == BookingNotice.Cancellation
            ? "This booking:"
            : "To move or cancel this booking, open:";
}
