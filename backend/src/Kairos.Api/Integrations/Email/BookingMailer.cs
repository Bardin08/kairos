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
/// One composer for all three notices. What differs is the <see cref="MailBody"/> each builds and
/// the METHOD; the MIME shape, transfer encodings, Message-Id and rendering zone are identical and
/// are the delicate parts — three composers would be three places for the folded .ics to get its
/// encoding wrong.
/// </remarks>
public sealed class BookingMailer(
    EmailSender sender,
    IcsBuilder calendar,
    MailHtmlRenderer html,
    IOptions<EmailOptions> options,
    ILogger<BookingMailer> logger)
{
    /// <summary>The route the frontend claims: <c>/manage/:token</c>, keyed on the token alone.</summary>
    private const string ManagePath = "manage";

    /// <summary>And <c>/proposal/:token</c>, keyed on the proposal's own token.</summary>
    private const string ProposalPath = "proposal";

    /// <summary>And <c>/book/:slug</c>, the public page a cancelled invitee books a new time from.</summary>
    private const string BookPath = "book";

    /// <summary>
    /// What the manage link is pressed under, in every notice that has a token to build one from.
    /// </summary>
    private const string ManageLabel = "Manage or cancel";

    private const string LinkLabel = "Link";
    private const string DurationLabel = "Duration";

    /// <summary>
    /// Where the meeting is joined. The value names no provider and could not: the mailer is
    /// handed a URL and nothing else, and rule 13 keeps provider names out of this tree anyway.
    /// </summary>
    private const string WhereLabel = "Where";

    private const string WhereValue = "Video call";

    private const string CalendarSubtype = "calendar";
    private const string AttachmentMediaType = "application";
    private const string AttachmentSubtype = "ics";
    private const string AttachmentFileName = "invite.ics";
    private const string MethodParameter = "method";
    private const string AlternativeSubtype = "alternative";
    private const string MixedSubtype = "mixed";

    /// <summary>The booking exists. <c>METHOD:REQUEST</c>, and the invitee's first sight of it.</summary>
    /// <returns>
    /// Whether the relay took it, the way <see cref="SendProposalAsync"/> already reports and for
    /// the same reason: a caller that retries — <see cref="Bookings.FollowUpWorker"/> — has to be
    /// able to tell a refusal from a send, and a caller with nowhere to retry is free to discard it,
    /// as the inline endpoints still do.
    /// </returns>
    public Task<bool> SendConfirmationAsync(
        Booking booking,
        MeetingIdentity identity,
        string manageToken,
        string? joinUrl,
        CancellationToken cancellationToken) =>
        SendAsync(
            BookingNotice.Confirmation, booking, identity, manageToken, joinUrl,
            previousStart: null, cancellationToken);

    /// <summary>
    /// <c>METHOD:REQUEST</c> again, same <c>UID</c>, with a <see cref="Booking.Sequence"/> the
    /// caller has already advanced — which is what makes a client update the meeting it holds
    /// rather than show a second invitation. A stale sequence is discarded without a word, so
    /// nobody reports the reschedule that did not arrive and the invitee turns up at the old time.
    /// </summary>
    /// <param name="previousStart">
    /// The time the meeting moved FROM, struck through in the message so the invitee can see which
    /// of the two hours in their inbox is the live one. Read before the move or it is the new time.
    /// </param>
    /// <remarks>
    /// The token is nullable for the same reason a cancellation's is, and became so when a second
    /// caller appeared: accepting a proposal arrives holding the PROPOSAL's token, and the manage
    /// one cannot be recovered from a stored SHA-256. The mail goes without the link.
    /// </remarks>
    /// <returns>Whether the relay took it — see <see cref="SendConfirmationAsync"/>.</returns>
    public Task<bool> SendRescheduleAsync(
        Booking booking,
        MeetingIdentity identity,
        string? manageToken,
        string? joinUrl,
        Instant? previousStart,
        CancellationToken cancellationToken) =>
        SendAsync(
            BookingNotice.Reschedule, booking, identity, manageToken, joinUrl, previousStart,
            cancellationToken);

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
    /// <returns>Whether the relay took it — see <see cref="SendConfirmationAsync"/>.</returns>
    public Task<bool> SendCancellationAsync(
        Booking booking,
        MeetingIdentity identity,
        string? manageToken,
        CancellationToken cancellationToken) =>
        SendAsync(
            BookingNotice.Cancellation,
            booking,
            identity,
            manageToken,
            joinUrl: null,
            previousStart: null,
            cancellationToken);

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
            () => ComposeProposalAsync(booking, proposal, proposalToken, cancellationToken),
            cancellationToken);

    /// <summary>The 24-hour notice — see <see cref="BookingNotice.Reminder"/>.</summary>
    /// <returns>Whether the relay took it — see <see cref="SendConfirmationAsync"/>.</returns>
    public Task<bool> SendReminderAsync(
        Booking booking,
        string manageToken,
        string? joinUrl,
        CancellationToken cancellationToken) =>
        SendComposedAsync(
            BookingNotice.Reminder,
            booking,
            () => ComposeReminderAsync(booking, manageToken, joinUrl, cancellationToken),
            cancellationToken);

    private Task<bool> SendAsync(
        BookingNotice notice,
        Booking booking,
        MeetingIdentity identity,
        string? manageToken,
        string? joinUrl,
        Instant? previousStart,
        CancellationToken cancellationToken) =>
        SendComposedAsync(
            notice,
            booking,
            () => ComposeAsync(
                booking, identity, manageToken, joinUrl, previousStart, notice, cancellationToken),
            cancellationToken);

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
    /// Every failure is logged at Warning with the booking id, whether or not anything retries it.
    /// The inline endpoints discard the bool this returns and give the organizer a resend by hand;
    /// <see cref="Bookings.FollowUpWorker"/> reads it and tries again on a later pass, which is why
    /// the token this composes from now reaches the worker encrypted rather than only hashed — the
    /// row the outbox pattern would need to retry a send is exactly what
    /// <see cref="Persistence.Entities.FollowUp"/> is.
    /// </remarks>
    private async Task<bool> SendComposedAsync(
        BookingNotice notice,
        Booking booking,
        Func<Task<MimeMessage>> compose,
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
            var message = await compose();
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
    /// │   ├── text/plain                          the body a filter and a watch read
    /// │   ├── text/html                           the same message, rendered
    /// │   └── text/calendar; method=REQUEST       what draws the accept/decline control
    /// └── application/ics (attachment)            what clients that ignore the part above save
    /// </code>
    /// The calendar part inside the alternative is what Gmail and Outlook.com read; the
    /// attachment is what Apple Mail and desktop clients act on. Both carry the same bytes.
    ///
    /// <paramref name="identity"/> is an argument rather than a lookup, exactly like
    /// <paramref name="joinUrl"/> beside it: this class composes and does not read, which is what
    /// keeps every assertion about it an assertion about bytes.
    /// </remarks>
    public async Task<MimeMessage> ComposeAsync(
        Booking booking,
        MeetingIdentity identity,
        string? manageToken,
        string? joinUrl,
        Instant? previousStart,
        BookingNotice notice,
        CancellationToken cancellationToken)
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
            Uid: identity.Uid,
            Start: start,
            End: end,
            Summary: booking.Link.Name,
            Description: manageUrl is null ? string.Empty : $"Manage or cancel: {manageUrl}",
            // The connected calendar account owns the meeting when one placed the event, and Kairos
            // is then only the address that posted the message. With no provider the sending
            // address owns it, and a SENT-BY naming the organizer would say nothing.
            OrganizerEmail: identity.Owner ?? organizer.Address,
            AttendeeEmail: booker.Email,
            Sequence: booking.Sequence,
            Method: method,
            Location: joinUrl,
            SentBy: identity.Owner is null ? null : organizer.Address));

        var fact = Fact(start, end, zone, previousStart);
        var mail = notice switch
        {
            BookingNotice.Confirmation => Confirmation(booking, booker.Name, fact, manageUrl, joinUrl),
            BookingNotice.Reschedule => Reschedule(booking, booker.Name, fact, manageUrl, joinUrl),
            BookingNotice.Cancellation => Cancellation(
                booking, booker.Name, fact, manageUrl, BookUrl(settings.PublicBaseUrl!, booking.Link.Slug)),
            BookingNotice.Proposal => throw ComposedElsewhere(notice, nameof(ComposeProposalAsync)),
            BookingNotice.Reminder => throw ComposedElsewhere(notice, nameof(ComposeReminderAsync)),
            _ => throw new ArgumentOutOfRangeException(nameof(notice), notice, "Unknown booking notice."),
        };

        var text = new TextPart(TextFormat.Plain);
        text.SetText(Encoding.UTF8, MailTextRenderer.Render(mail));

        var markup = new TextPart(TextFormat.Html);
        markup.SetText(Encoding.UTF8, await html.RenderAsync(mail, cancellationToken));

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
                // Order is the guarantee: a client renders the LAST part it understands, so the
                // calendar part stays after the HTML or the accept/decline control is lost to it.
                new Multipart(AlternativeSubtype) { text, markup, invitation },
                attachment,
            },
            organizer,
            Mailbox(booker));
    }

    /// <remarks>
    /// No <c>text/calendar</c> part and no attachment, by design: a calendar part is applied by
    /// the client, so attaching one would move the meeting the message exists to ask about. The
    /// link is the whole interface.
    ///
    /// One action and no secondary, where the design showed a "keep the original" link beside it:
    /// the page that link would open reads no query string, so it would land the invitee on the
    /// same screen and decide nothing. The one action opens that page, which carries both buttons.
    /// </remarks>
    public async Task<MimeMessage> ComposeProposalAsync(
        Booking booking,
        BookingProposal proposal,
        string proposalToken,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var booker = booking.Participants.First(participant => participant.Role == ParticipantRole.Booker);
        var organizer = MailboxAddress.Parse(settings.From!);
        var zone = DateTimeZoneProviders.Tzdb[booker.TimeZoneId];

        var current = Instant.FromUnixTimeSeconds(booking.StartUtc).InZone(zone);
        var proposed = Instant.FromUnixTimeSeconds(proposal.StartUtc).InZone(zone);
        var proposedEnd = Instant.FromUnixTimeSeconds(proposal.EndUtc).InZone(zone);
        var expires = Instant.FromUnixTimeSeconds(proposal.ExpiresAt).InZone(zone);

        var mail = new MailBody
        {
            Headline = "A new time is suggested",

            // The PROPOSED time in the block and the current one struck through beside it, which
            // is the opposite of every other notice: this message is about a time nothing holds.
            Fact = new MailFact(
                $"{OutgoingMail.Time(proposed)}-{OutgoingMail.Time(proposedEnd)}",
                OutgoingMail.Date(proposed),
                zone.Id,
                Moment(current)),
            Sentence =
                $"Hi {booker.Name} — nothing has changed yet. Your meeting stays exactly where it is "
                + "unless you accept.",
            Action = new MailAction(
                "Accept the new time", ProposalUrl(settings.PublicBaseUrl!, proposalToken)),
            Details = [new MailDetail(LinkLabel, booking.Link.Name)],
            Footer =
                $"If you do not answer by {Moment(expires)} ({zone.Id}), the suggestion lapses and "
                + "the meeting stays exactly where it is.",
        };

        var text = new TextPart(TextFormat.Plain);
        text.SetText(Encoding.UTF8, MailTextRenderer.Render(mail));

        var markup = new TextPart(TextFormat.Html);
        markup.SetText(Encoding.UTF8, await html.RenderAsync(mail, cancellationToken));

        return OutgoingMail.Envelope(
            $"A new time for {booking.Link.Name}?",
            new Multipart(AlternativeSubtype) { text, markup },
            organizer,
            Mailbox(booker));
    }

    /// <remarks>
    /// No <c>text/calendar</c> part and no attachment, for the same structural reason as
    /// <see cref="ComposeProposalAsync"/>: the meeting has not changed, so a calendar part here
    /// would restate an event the invitee's client already holds rather than tell them anything.
    ///
    /// The join URL is the action here and nowhere else: this is the one message that arrives when
    /// joining is the next thing the invitee does, where every other notice arrives when the next
    /// thing is to check or cancel. A booking with no conference has nothing to make the action,
    /// so the manage link takes its place — the same fallback <see cref="Manage"/> already gives
    /// every other notice — and there is nothing left over to demote to a secondary.
    /// </remarks>
    public async Task<MimeMessage> ComposeReminderAsync(
        Booking booking,
        string manageToken,
        string? joinUrl,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var booker = booking.Participants.First(participant => participant.Role == ParticipantRole.Booker);
        var organizer = MailboxAddress.Parse(settings.From!);
        var zone = DateTimeZoneProviders.Tzdb[booker.TimeZoneId];

        var start = Instant.FromUnixTimeSeconds(booking.StartUtc);
        var end = Instant.FromUnixTimeSeconds(booking.EndUtc);
        var local = start.InZone(zone);
        var manageUrl = ManageUrl(settings.PublicBaseUrl!, manageToken);

        var mail = new MailBody
        {
            Headline = "Tomorrow",
            Fact = Fact(start, end, zone, previousStart: null),
            Sentence =
                $"Hi {booker.Name} — a reminder that this is tomorrow. Nothing to do; the joining "
                + "link is below.",
            Action = joinUrl is null
                ? new MailAction(ManageLabel, manageUrl)
                : new MailAction("Join the meeting", joinUrl),
            Secondary = joinUrl is null ? null : new MailSecondary(ManageLabel, manageUrl),
            Details = Details(booking, joinUrl: null),
        };

        var text = new TextPart(TextFormat.Plain);
        text.SetText(Encoding.UTF8, MailTextRenderer.Render(mail));

        var markup = new TextPart(TextFormat.Html);
        markup.SetText(Encoding.UTF8, await html.RenderAsync(mail, cancellationToken));

        return OutgoingMail.Envelope(
            $"Tomorrow: {booking.Link.Name}, {OutgoingMail.Time(local)} ({zone.Id})",
            new Multipart(AlternativeSubtype) { text, markup },
            organizer,
            Mailbox(booker));
    }

    private static MailBody Confirmation(
        Booking booking, string bookerName, MailFact fact, string? manageUrl, string? joinUrl) =>
        new()
        {
            Headline = "You are booked",
            Fact = fact,
            Sentence =
                $"Hi {bookerName} — {booking.Link.Name} is confirmed. The invitation is attached, so "
                + "your calendar will pick it up.",
            Action = Manage(manageUrl),
            Details = Details(booking, joinUrl),
        };

    private static MailBody Reschedule(
        Booking booking, string bookerName, MailFact fact, string? manageUrl, string? joinUrl) =>
        new()
        {
            Headline = "Your meeting moved",
            Fact = fact,
            Sentence =
                $"Hi {bookerName} — {booking.Link.Name} has moved. Your calendar entry has been "
                + "updated in place, so there is nothing to accept again.",
            Action = Manage(manageUrl),
            Details = Details(booking, joinUrl),
        };

    /// <remarks>
    /// No join row, where the other two notices carry one: the meeting is off and there is nothing
    /// left to join. The way back is the secondary link, which is the public booking page rather
    /// than a slot — the invitee is choosing again, not confirming.
    ///
    /// The reason is the operator's own words and the point of an operator's cancel. Absent from a
    /// cancellation the invitee triggered themselves, and its heading goes with it.
    /// </remarks>
    private static MailBody Cancellation(
        Booking booking, string bookerName, MailFact fact, string? manageUrl, string bookUrl) =>
        new()
        {
            Headline = "Your meeting is cancelled",
            Fact = fact,
            Sentence =
                $"Hi {bookerName} — {booking.Link.Name} is off and the time has been released. Your "
                + "calendar entry has been removed.",
            Action = Manage(manageUrl),
            Secondary = new MailSecondary("Book another time", bookUrl),
            Details = Details(booking, joinUrl: null),
            Reason = string.IsNullOrWhiteSpace(booking.CancellationReason)
                ? null
                : booking.CancellationReason,
            Footer = "Nothing further is needed from you.",
        };

    /// <summary>
    /// The manage link, in every notice that has a token to build one from — the cancellation
    /// included, because it is the only way an invitee can check that a cancellation they are
    /// reading about is real. A notice the operator triggered has no token and drops the block
    /// whole rather than inventing a link.
    /// </summary>
    private static MailAction? Manage(string? manageUrl) =>
        manageUrl is null ? null : new MailAction(ManageLabel, manageUrl);

    /// <summary>
    /// What every booking notice states under the fold, in one order. The join URL is a detail and
    /// never the action: a confirmation arrives days out, when the thing to do is check or cancel.
    /// </summary>
    private static List<MailDetail> Details(Booking booking, string? joinUrl)
    {
        var minutes = (int)Duration.FromSeconds(booking.EndUtc - booking.StartUtc).TotalMinutes;
        List<MailDetail> details =
        [
            new(LinkLabel, booking.Link.Name),
            new(DurationLabel, $"{minutes} minutes"),
        ];

        if (joinUrl is not null)
        {
            details.Add(new MailDetail(WhereLabel, WhereValue, joinUrl));
        }

        return details;
    }

    private static MailFact Fact(Instant start, Instant end, DateTimeZone zone, Instant? previousStart)
    {
        var local = start.InZone(zone);

        return new MailFact(
            $"{OutgoingMail.Time(local)}-{OutgoingMail.Time(end.InZone(zone))}",
            OutgoingMail.Date(local),
            zone.Id,
            previousStart is { } previous ? Moment(previous.InZone(zone)) : null);
    }

    /// <summary>
    /// A day and an hour on one line. The time a meeting moved from is written this way in both
    /// notices that show one, because a struck-through date over a struck-through hour reads as
    /// two cancelled things rather than one.
    /// </summary>
    private static string Moment(ZonedDateTime value) =>
        $"{OutgoingMail.Date(value)}, {OutgoingMail.Time(value)}";

    private static MailboxAddress Mailbox(BookingParticipant booker) => new(booker.Name, booker.Email);

    /// <summary>
    /// The token is escaped and the base trimmed, so a base URL written with a trailing slash does
    /// not produce a double one that some mail clients decline to linkify.
    /// </summary>
    private static string ManageUrl(string baseUrl, string manageToken) =>
        OutgoingMail.Url(baseUrl, $"{ManagePath}/{Uri.EscapeDataString(manageToken)}");

    private static string ProposalUrl(string baseUrl, string proposalToken) =>
        OutgoingMail.Url(baseUrl, $"{ProposalPath}/{Uri.EscapeDataString(proposalToken)}");

    private static string BookUrl(string baseUrl, string slug) =>
        OutgoingMail.Url(baseUrl, $"{BookPath}/{Uri.EscapeDataString(slug)}");

    /// <summary>
    /// Unreachable through <see cref="SendProposalAsync"/> or <see cref="SendReminderAsync"/>,
    /// which route to <see cref="ComposeProposalAsync"/> and <see cref="ComposeReminderAsync"/>
    /// respectively. Spelled out anyway because the discard arm's "unknown booking notice" is a
    /// lie here: the notice is known and this is the wrong composer for it.
    /// </summary>
    private static ArgumentOutOfRangeException ComposedElsewhere(BookingNotice notice, string composer) =>
        new(nameof(notice), notice, $"{notice} carries no calendar part; compose it with {composer}.");

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
            BookingNotice.Proposal => throw ComposedElsewhere(notice, nameof(ComposeProposalAsync)),
            BookingNotice.Reminder => throw ComposedElsewhere(notice, nameof(ComposeReminderAsync)),
            _ => throw new ArgumentOutOfRangeException(nameof(notice), notice, "Unknown booking notice."),
        };

        return $"{lead}: {linkName}, {OutgoingMail.Date(local)} at {OutgoingMail.Time(local)} ({zone.Id})";
    }
}
