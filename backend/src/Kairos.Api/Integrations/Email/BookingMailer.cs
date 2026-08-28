using System.Text;

using Kairos.Api.Persistence.Entities;

using Microsoft.Extensions.Options;

using MimeKit;
using MimeKit.Text;
using MimeKit.Utils;

using NodaTime;
using NodaTime.Text;

namespace Kairos.Api.Integrations.Email;

/// <remarks>
/// Composition is separated from sending because composition is the half that can be proved: the
/// MIME structure, the METHOD/<c>method=</c> agreement and the manage link are all assertable
/// without a relay. Inbox placement is not, and is verified by sending real mail.
///
/// One composer for all three notices. What differs is three strings and the METHOD; the MIME
/// shape, transfer encodings, Message-Id and rendering zone are identical and are the delicate
/// parts — three composers would be three places for the folded .ics to get its encoding wrong.
/// </remarks>
public sealed class BookingMailer(
    EmailSender sender,
    IcsBuilder calendar,
    IOptions<EmailOptions> options,
    ILogger<BookingMailer> logger)
{
    /// <summary>The route the frontend claims: <c>/manage/:token</c>, keyed on the token alone.</summary>
    private const string ManagePath = "manage";

    /// <summary>And <c>/proposal/:token</c>, keyed on the proposal's own token.</summary>
    private const string ProposalPath = "proposal";

    /// <summary>
    /// What the operator's words are printed under. Public so a test can assert its absence: the
    /// claim being made is that an empty reason leaves no heading, and a test spelling the string
    /// itself would go on passing after the heading was reworded.
    /// </summary>
    public const string ReasonHeading = "Why:";

    /// <summary>One blank line between blocks, which is what makes an absent block leave no mark.</summary>
    private const string BlockSeparator = "\n\n";

    private const string CalendarSubtype = "calendar";
    private const string AttachmentMediaType = "application";
    private const string AttachmentSubtype = "ics";
    private const string AttachmentFileName = "invite.ics";
    private const string MethodParameter = "method";
    private const string AlternativeSubtype = "alternative";
    private const string MixedSubtype = "mixed";

    /// <summary>The booking exists. <c>METHOD:REQUEST</c>, and the invitee's first sight of it.</summary>
    public Task SendConfirmationAsync(
        Booking booking,
        string manageToken,
        string? joinUrl,
        CancellationToken cancellationToken) =>
        SendAsync(BookingNotice.Confirmation, booking, manageToken, joinUrl, cancellationToken);

    /// <summary>
    /// <c>METHOD:REQUEST</c> again, same <c>UID</c>, with a <see cref="Booking.Sequence"/> the
    /// caller has already advanced — which is what makes a client update the meeting it holds
    /// rather than show a second invitation. A stale sequence is discarded without a word, so
    /// nobody reports the reschedule that did not arrive and the invitee turns up at the old time.
    /// </summary>
    /// <remarks>
    /// The token is nullable for the same reason a cancellation's is, and became so when a second
    /// caller appeared: accepting a proposal arrives holding the PROPOSAL's token, and the manage
    /// one cannot be recovered from a stored SHA-256. The mail goes without the link.
    /// </remarks>
    public Task SendRescheduleAsync(
        Booking booking,
        string? manageToken,
        string? joinUrl,
        CancellationToken cancellationToken) =>
        SendAsync(BookingNotice.Reschedule, booking, manageToken, joinUrl, cancellationToken);

    /// <summary>
    /// <c>METHOD:CANCEL</c>, which <see cref="IcsBuilder"/> already pairs with
    /// <c>STATUS:CANCELLED</c> — both, because Outlook removes the event on the status and leaves
    /// one marked CONFIRMED in place however the message was labelled.
    /// </summary>
    /// <remarks>
    /// A cancellation the operator triggered has no plaintext token and no way to recover one. The
    /// confirmation keeps <c>string</c> deliberately: it mints the manage token, which appears in
    /// no later response.
    ///
    /// No join URL is taken, where the other two notices take one: this message removes the event,
    /// so there is nothing left to join.
    /// </remarks>
    public Task SendCancellationAsync(
        Booking booking,
        string? manageToken,
        CancellationToken cancellationToken) =>
        SendAsync(BookingNotice.Cancellation, booking, manageToken, joinUrl: null, cancellationToken);

    /// <summary>
    /// The operator has offered a new time. Nothing has moved; the invitee decides.
    /// </summary>
    /// <returns>
    /// Whether it went — the one notice that has to report this. A proposal's token exists only
    /// inside the call that minted it, so a hold whose mail was swallowed can never be answered
    /// and keeps its slot for the whole window. The other three describe a booking that stands
    /// whether or not the message arrived; this one IS the message.
    /// </returns>
    public Task<bool> SendProposalAsync(
        Booking booking,
        BookingProposal proposal,
        string proposalToken,
        CancellationToken cancellationToken) =>
        SendComposedAsync(
            BookingNotice.Proposal,
            booking,
            () => ComposeProposal(booking, proposal, proposalToken),
            cancellationToken);

    private Task SendAsync(
        BookingNotice notice,
        Booking booking,
        string? manageToken,
        string? joinUrl,
        CancellationToken cancellationToken) =>
        SendComposedAsync(
            notice, booking, () => Compose(booking, manageToken, joinUrl, notice), cancellationToken);

    /// <summary>
    /// Composition is deferred rather than passed in already done, so that this stays the one home
    /// for the configured check, the try and the logging: rule 14 is a promise about every notice,
    /// and a second copy of the swallow is a second place for one of them to stop keeping it.
    /// </summary>
    /// <remarks>
    /// Ground rule 14, at the only place it can be enforced: the booking has been written, moved
    /// or cancelled before this is called, and nothing here can undo it. Both ways out are silent
    /// — an unconfigured relay is the ordinary state of a fresh deployment, and a relay that
    /// refused is a delivery problem, not a booking one.
    ///
    /// The trade: a failed send is not retried and the invitee gets nothing, logged at Warning
    /// with the booking id so the organizer can resend by hand. The outbox would give retries free
    /// and is the right home for the webhook, but an email row would have to carry a raw token in
    /// its payload, and Part A stores only those tokens' SHA-256 — persisting the plaintext would
    /// make one database read worth cancel-and-reschedule on every booking in the file. So every
    /// send runs on the request that caused it, where the plaintext is in hand.
    /// </remarks>
    private async Task<bool> SendComposedAsync(
        BookingNotice notice,
        Booking booking,
        Func<MimeMessage> compose,
        CancellationToken cancellationToken)
    {
        if (!options.Value.IsConfigured)
        {
            logger.LogDebug(
                "{Section} is not configured; booking {Booking} changed without a {Notice} being sent.",
                EmailOptions.SectionName,
                booking.Id,
                notice);
            return false;
        }

        try
        {
            var message = compose();
            var response = await sender.SendAsync(message, cancellationToken);

            // Said at Information, not Debug. Silence on the happy path is what let a message
            // Kairos had not sent be read twice as proof that Kairos could send. The Message-Id
            // and the relay's response settle that from a log line alone; the sequence is the
            // third handle, deciding whether a client acts on the message or drops it.
            logger.LogInformation(
                "{Notice} for booking {Booking} at sequence {Sequence} accepted by the relay as "
                + "{MessageId}: {Response}",
                notice,
                booking.Id,
                booking.Sequence,
                message.MessageId,
                response);
            return true;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                exception,
                "Could not send the {Notice} for booking {Booking}. The booking stands as it is and "
                + "the invitee has not been told; there is no retry, so resend by hand if it matters.",
                notice,
                booking.Id);
            return false;
        }
    }

    /// <remarks>
    /// The structure is Google Calendar's own — the shape Gmail and Outlook are both known to
    /// render an accept/decline control from:
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
    public MimeMessage Compose(
        Booking booking, string? manageToken, string? joinUrl, BookingNotice notice)
    {
        var settings = options.Value;
        var booker = booking.Participants.First(participant => participant.Role == ParticipantRole.Booker);
        var organizer = MailboxAddress.Parse(settings.From!);

        // Null for anything the operator triggered: only the SHA-256 is stored, so a cancel pressed
        // in the workbench has no plaintext to build a link from. The mail goes without the link
        // rather than not going.
        var manageUrl = manageToken is null ? null : ManageUrl(settings.PublicBaseUrl!, manageToken);

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
            Description: manageUrl is null ? string.Empty : $"Manage or cancel: {manageUrl}",
            OrganizerEmail: organizer.Address,
            AttendeeEmail: booker.Email,
            Sequence: booking.Sequence,
            Method: method,
            Location: joinUrl));

        var body = new TextPart(TextFormat.Plain);
        body.SetText(
            Encoding.UTF8,
            Body(
                notice,
                booker.Name,
                booking.Link.Name,
                start,
                end,
                zone,
                manageUrl,
                booking.CancellationReason));

        var invitation = new TextPart(CalendarSubtype)
        {
            // Base64 rather than left to MimeKit's choice. The .ics is folded at 75 octets with a
            // single leading space per continuation, and any encoding that normalises whitespace
            // or line endings unfolds it into a file no client will parse.
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

        return OutgoingMail.Envelope(
            Subject(notice, booking.Link.Name, start, zone),
            new Multipart(MixedSubtype)
            {
                new Multipart(AlternativeSubtype) { body, invitation },
                attachment,
            },
            organizer,
            Mailbox(booker));
    }

    /// <remarks>
    /// No <c>text/calendar</c> part and no attachment, by design: a calendar part is applied by
    /// the client, so attaching one would move the meeting the message exists to ask about. The
    /// link is the whole interface.
    /// </remarks>
    public MimeMessage ComposeProposal(Booking booking, BookingProposal proposal, string proposalToken)
    {
        var settings = options.Value;
        var booker = booking.Participants.First(participant => participant.Role == ParticipantRole.Booker);
        var organizer = MailboxAddress.Parse(settings.From!);
        var zone = DateTimeZoneProviders.Tzdb[booker.TimeZoneId];

        var current = Instant.FromUnixTimeSeconds(booking.StartUtc).InZone(zone);
        var proposed = Instant.FromUnixTimeSeconds(proposal.StartUtc).InZone(zone);
        var proposedEnd = Instant.FromUnixTimeSeconds(proposal.EndUtc).InZone(zone);
        var expires = Instant.FromUnixTimeSeconds(proposal.ExpiresAt).InZone(zone);
        var url = ProposalUrl(settings.PublicBaseUrl!, proposalToken);

        var body = new TextPart(TextFormat.Plain);
        body.SetText(Encoding.UTF8, $"""
            Hi {booker.Name},

            {booking.Link.Name} is currently {OutgoingMail.Date(current)} at {OutgoingMail.Time(current)} ({zone.Id}).

            A new time is being suggested:

            {OutgoingMail.Date(proposed)}
            {OutgoingMail.Time(proposed)}-{OutgoingMail.Time(proposedEnd)} ({zone.Id})

            Nothing has changed yet. Accept or decline here:
            {url}

            If you do not answer by {OutgoingMail.Date(expires)}, {OutgoingMail.Time(expires)} ({zone.Id}),
            the suggestion lapses and the meeting stays exactly where it is.
            """);

        return OutgoingMail.Envelope(
            $"A new time for {booking.Link.Name}?", body, organizer, Mailbox(booker));
    }

    private static MailboxAddress Mailbox(BookingParticipant booker) => new(booker.Name, booker.Email);

    /// <summary>
    /// The token is escaped and the base trimmed, so a base URL written with a trailing slash does
    /// not produce a double one that some mail clients decline to linkify.
    /// </summary>
    private static string ManageUrl(string baseUrl, string manageToken) =>
        OutgoingMail.Url(baseUrl, $"{ManagePath}/{Uri.EscapeDataString(manageToken)}");

    private static string ProposalUrl(string baseUrl, string proposalToken) =>
        OutgoingMail.Url(baseUrl, $"{ProposalPath}/{Uri.EscapeDataString(proposalToken)}");

    /// <summary>
    /// Unreachable through <see cref="SendProposalAsync"/>, which routes to
    /// <see cref="ComposeProposal"/>. Spelled out anyway because the discard arm's "unknown
    /// booking notice" is a lie here: the notice is known and this is the wrong composer for it.
    /// </summary>
    private static ArgumentOutOfRangeException ComposedElsewhere(BookingNotice notice) =>
        new(
            nameof(notice),
            notice,
            $"A proposal carries no calendar part; compose it with {nameof(ComposeProposal)}.");

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
            BookingNotice.Proposal => throw ComposedElsewhere(notice),
            _ => throw new ArgumentOutOfRangeException(nameof(notice), notice, "Unknown booking notice."),
        };

        return $"{lead}: {linkName}, {OutgoingMail.Date(local)} at {OutgoingMail.Time(local)} ({zone.Id})";
    }

    /// <summary>
    /// Plain text, and no HTML alternative. There is nothing here that needs a font, and a
    /// text-only body is one fewer thing for a filter to score.
    /// </summary>
    /// <remarks>
    /// Assembled out of blocks joined by one blank line rather than a template with holes in it:
    /// two blocks are optional, and a template prints an empty line where an absent one was and a
    /// heading over nothing where its label was.
    ///
    /// The manage link is in every notice that has a token to build one from, the cancellation
    /// included — it is the only way an invitee can check that a cancellation they are reading
    /// about is real. A notice the operator triggered has no token and drops the block whole.
    ///
    /// The reason is the operator's own words and the point of an operator's cancel. Absent from
    /// notices the invitee triggered themselves, and absent leaves the mail byte-for-byte as it
    /// was before this existed.
    /// </remarks>
    private static string Body(
        BookingNotice notice,
        string bookerName,
        string linkName,
        Instant start,
        Instant end,
        DateTimeZone zone,
        string? manageUrl,
        string? reason)
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
            BookingNotice.Proposal => throw ComposedElsewhere(notice),
            _ => throw new ArgumentOutOfRangeException(nameof(notice), notice, "Unknown booking notice."),
        };

        var local = start.InZone(zone);
        var times = $"""
            {OutgoingMail.Date(local)}
            {OutgoingMail.Time(local)}-{OutgoingMail.Time(end.InZone(zone))} ({zone.Id})
            """;

        var explanation = string.IsNullOrWhiteSpace(reason)
            ? null
            : $"""
                {ReasonHeading}
                {reason}
                """;

        var action = manageUrl is null
            ? null
            : $"""
                {Action(notice)}
                {manageUrl}
                """;

        string?[] blocks = [$"Hi {bookerName},", headline, times, explanation, action, closing];

        return string.Join(BlockSeparator, blocks.Where(block => !string.IsNullOrEmpty(block)));
    }

    private static string Action(BookingNotice notice) =>
        notice == BookingNotice.Cancellation
            ? "This booking:"
            : "To move or cancel this booking, open:";
}
