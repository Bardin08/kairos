using System.Text;

using Kairos.Api.Integrations.Email;
using Kairos.Api.Persistence.Entities;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using MimeKit;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Integrations;

/// <summary>
/// What the confirmation is made of, asserted on the bytes that go on the wire.
/// </summary>
/// <remarks>
/// Every message under test is written out and parsed back before anything is read off it, so
/// what is asserted is what a relay would hand a client rather than the object graph that
/// produced it — transfer encodings included, which is where a folded .ics gets destroyed.
///
/// Nothing here mocks SMTP. <see cref="EmailSender"/> is real in every one of these, and the one
/// test that reaches the network points it at a port nothing listens on: a mock would assert that
/// MailKit was called the way this file already knows it is called, which is a test of the test.
/// Whether a message lands in an inbox is not decidable here at all, and is verified by sending
/// real mail to a Gmail and an Outlook account.
/// </remarks>
public class BookingMailerTests
{
    private const string ManageToken = "3f2504e04f8911d39a0c0305e82c3301";
    private const string BaseUrl = "https://kairos.example.com";
    private const string ManageUrlFragment = "/manage/";
    private const string CancellationReason = "The client moved the workshop a week.";
    private const string OrganizerAddress = "organizer@example.com";
    private const string BookerAddress = "guest@example.com";
    private const string BookerName = "Vlad";
    private const string LinkName = "Consulting call";
    private const string BookerZone = "Europe/Warsaw";

    /// <summary>RFC 5545 §3.1 — the octet budget a content line may not exceed.</summary>
    private const int MaxLineOctets = 75;

    /// <summary>
    /// Reserved by IANA and bindable by nothing on any platform, so a connection to it is refused
    /// at once rather than after a timeout. This is how the failure path is reached without a
    /// relay and without a mock.
    /// </summary>
    private const int PortNothingListensOn = 1;

    private const int DurationMinutes = 25;

    /// <summary>The heading the mailer writes over a cancellation's manage link.</summary>
    private const string CancellationAction = "This booking:";

    /// <summary>
    /// Two blank lines in a row. Nothing in this mail is separated by more than one, so a run of
    /// them is the mark an omitted block left behind.
    /// </summary>
    private const string BlankLineRun = "\n\n\n";

    private static readonly Instant Start = Instant.FromUtc(2026, 9, 7, 7, 0);

    [Fact]
    public void Compose_Always_AttachesTheIcsAsACalendarRequest()
    {
        var calendar = CalendarPart(Compose());

        calendar.ContentType.MediaType.ShouldBe("text");
        calendar.ContentType.MediaSubtype.ShouldBe("calendar");
        calendar.ContentType.Parameters["method"].ShouldBe("REQUEST");
    }

    /// <summary>
    /// The rule the whole structure turns on. A client reads the MIME parameter to decide what
    /// kind of message this is and the .ics METHOD to decide what to do with the event; when they
    /// disagree it drops the update without a word, exactly as it does for a stale SEQUENCE.
    /// </summary>
    [Fact]
    public void Compose_Always_MakesTheMimeMethodAgreeWithTheIcsMethod()
    {
        var message = Compose();

        Ics(message).ShouldContain($"METHOD:{CalendarPart(message).ContentType.Parameters["method"]}");
    }

    /// <summary>
    /// Not a second .ics writer. The UID is the booking's own identifier and the SEQUENCE is the
    /// booking's own, which is what makes a rescheduled meeting land as a change rather than as a
    /// second invitation.
    /// </summary>
    /// <remarks>
    /// REQUEST, not some third method: a move is the same invitation at a new time, and that is
    /// the entire mechanism by which a client updates the event it is holding. Get the method or
    /// the sequence wrong and nothing is reported — the update is dropped, and the invitee arrives
    /// at the hour of the invitation they still have.
    /// </remarks>
    [Fact]
    public void Compose_Reschedule_IsARequestCarryingTheSameUidAtTheNewSequence()
    {
        var booking = Booking(sequence: 1);

        var message = Compose(booking, notice: BookingNotice.Reschedule);
        var ics = Ics(message);

        ics.ShouldContain($"UID:{booking.Id}");
        ics.ShouldContain("SEQUENCE:1");
        ics.ShouldContain("METHOD:REQUEST");
        CalendarPart(message).ContentType.Parameters["method"].ShouldBe("REQUEST");
        message.Subject.ShouldStartWith("Moved:");
    }

    /// <summary>
    /// The cancellation, in the three lines that have to agree for a client to remove the event.
    /// METHOD:CANCEL alone is not enough — Outlook acts on STATUS:CANCELLED and leaves an event
    /// marked CONFIRMED sitting in the calendar however the message was labelled — and a CANCEL
    /// body inside a part labelled REQUEST is discarded without a word to either side.
    /// </summary>
    [Fact]
    public void Compose_Cancellation_IsACancelWhoseStatusAndMimeMethodBothAgree()
    {
        var booking = Booking(sequence: 2);

        var message = Compose(booking, notice: BookingNotice.Cancellation);
        var ics = Ics(message);

        ics.ShouldContain("METHOD:CANCEL");
        ics.ShouldContain("STATUS:CANCELLED");
        ics.ShouldContain($"UID:{booking.Id}");
        ics.ShouldContain("SEQUENCE:2");
        CalendarPart(message).ContentType.Parameters["method"].ShouldBe("CANCEL");
        message.Subject.ShouldStartWith("Cancelled:");
    }

    /// <summary>
    /// An operator's cancellation has no manage token to put in the mail, and cannot get one:
    /// only the SHA-256 is stored. The notice still has to go, so the link is omitted rather than
    /// invented — and it is worth nothing on a cancelled booking anyway.
    /// </summary>
    /// <remarks>
    /// The heading is asserted alongside the URL, and that is the half that pins the behaviour
    /// this was written for. A body that dropped the link and kept "This booking:" over the empty
    /// space where it had been passes an assertion about the URL alone, and reads to the invitee
    /// as a mail with something missing from it.
    /// </remarks>
    [Fact]
    public void Compose_ACancellationWithNoManageToken_DropsTheWholeBlockAndStillCarriesTheCalendarPart()
    {
        var message = Compose(manageToken: null, notice: BookingNotice.Cancellation);
        var body = Plain(message);

        body.ShouldNotContain(ManageUrlFragment);
        body.ShouldNotContain(CancellationAction);

        // And no hole where the block was: the closing line follows the times directly, one blank
        // line apart, exactly as it does when there is nothing optional in the mail at all.
        body.ShouldNotContain(BlankLineRun);

        var ics = Ics(message);
        ics.ShouldContain("METHOD:CANCEL");
        ics.ShouldContain("STATUS:CANCELLED");
    }

    /// <summary>
    /// The whole point of an operator's cancel. The invitee is told a meeting is off by somebody
    /// who is not them, and the one thing that makes that not arbitrary is the sentence saying
    /// why — which the workbench promises them is read, on the panel that collects it.
    /// </summary>
    [Fact]
    public void Compose_ACancellationCarryingAReason_PrintsItUnderTheHeadline()
    {
        var booking = Booking();
        booking.CancellationReason = CancellationReason;

        var body = Plain(Compose(booking, notice: BookingNotice.Cancellation, manageToken: null));

        body.ShouldContain(BookingMailer.ReasonHeading);
        body.ShouldContain(CancellationReason);
    }

    /// <summary>
    /// The invitee's own cancel carries no reason — they know why — and the mail they get has to
    /// be what it was before this column existed. A heading over nothing is the failure mode a
    /// template with an optional block in it produces, and it is read by the person least able to
    /// ask what was meant by it.
    /// </summary>
    [Fact]
    public void Compose_ACancellationWithNoReason_PrintsNoHeadingOverNothing()
    {
        var body = Plain(Compose(notice: BookingNotice.Cancellation));

        body.ShouldNotContain(BookingMailer.ReasonHeading);
        body.ShouldNotContain(BlankLineRun);
    }

    /// <summary>
    /// The invitee's only route to cancelling or moving the meeting, in the shape Part A's
    /// frontend routes — the raw token in the path, and nothing else keyed alongside it.
    /// </summary>
    [Fact]
    public void Compose_Always_CarriesTheManageLinkInThePlainTextBody() =>
        PlainPart(Compose()).Text.ShouldContain($"{BaseUrl}/manage/{ManageToken}");

    /// <summary>
    /// The .ics carries it too, so the invitee who keeps only the calendar entry can still find
    /// their way back.
    /// </summary>
    [Fact]
    public void Compose_Always_CarriesTheManageLinkInTheCalendarDescription() =>
        Unfold(Ics(Compose())).ShouldContain($"{BaseUrl}/manage/{ManageToken}");

    /// <summary>
    /// A base URL written with a trailing slash is the ordinary way an operator writes one, and
    /// the double slash it would otherwise produce is enough for some clients to stop linkifying.
    /// </summary>
    [Fact]
    public void Compose_BaseUrlWithATrailingSlash_DoesNotProduceADoubleSlash() =>
        PlainPart(Compose(options: Options($"{BaseUrl}/"))).Text
            .ShouldContain($"{BaseUrl}/manage/{ManageToken}");

    /// <summary>
    /// The transfer encoding is the one thing between a correct .ics and a corrupt one. The
    /// builder folds at 75 octets with a leading space marking each continuation, and an encoding
    /// that normalises whitespace or line endings leaves a file no client will parse — which
    /// looks, from the invitee's side, exactly like an email with no accept button.
    /// </summary>
    [Fact]
    public void Compose_Always_KeepsTheFoldedIcsIntactThroughTheTransferEncoding()
    {
        var ics = Ics(Compose());

        ics.ShouldStartWith("BEGIN:VCALENDAR\r\n");
        ics.ShouldEndWith("END:VCALENDAR\r\n");
        foreach (var line in ics.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            Encoding.UTF8.GetByteCount(line).ShouldBeLessThanOrEqualTo(MaxLineOctets);
        }
    }

    /// <summary>
    /// Gmail and Outlook.com read the calendar part out of a multipart/alternative; Apple Mail
    /// and the desktop clients act on the attachment. Both are present and both carry the same
    /// bytes, because dropping either costs the accept/decline control on some client.
    /// </summary>
    [Fact]
    public void Compose_Always_OffersTheInvitationBothAsAnAlternativeAndAsAnAttachment()
    {
        var message = Compose();

        message.Body.ShouldBeOfType<Multipart>().ContentType.MediaSubtype.ShouldBe("mixed");
        CalendarPart(message).ShouldNotBeNull();

        var attachment = message.Attachments.OfType<MimePart>().Single();
        attachment.FileName.ShouldBe("invite.ics");
        Read(attachment).ShouldBe(Ics(message));
    }

    /// <summary>
    /// No ORGANIZER and no ATTENDEE means no accept/decline control, whatever the MIME type says.
    /// The organizer is the configured sending address, parsed out of it, so a From written with
    /// a display name still yields a bare mailto.
    /// </summary>
    [Fact]
    public void Compose_Always_NamesTheSenderAsOrganizerAndTheBookerAsAttendee()
    {
        var message = Compose(options: Options(BaseUrl, from: $"Kairos <{OrganizerAddress}>"));
        var ics = Unfold(Ics(message));

        message.From.Mailboxes.Single().Address.ShouldBe(OrganizerAddress);
        message.To.Mailboxes.Single().Address.ShouldBe(BookerAddress);
        ics.ShouldContain($"ORGANIZER:mailto:{OrganizerAddress}");
        ics.ShouldContain($"ATTENDEE;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:{BookerAddress}");
    }

    /// <summary>
    /// Left to MimeKit the right-hand side is the machine's hostname, which inside a container is
    /// a random hex string and not a domain that resolves. Filters score that, and this whole
    /// feature is judged on inbox placement rather than on a 250.
    /// </summary>
    [Fact]
    public void Compose_Always_StampsTheMessageIdWithTheSendingDomain() =>
        Compose().MessageId.ShouldEndWith("@example.com");

    /// <summary>
    /// The booker's zone, not the organizer's. 07:00Z is 09:00 in Warsaw, so a mail rendered in
    /// UTC — or in the organizer's zone — would tell the invitee the wrong hour and still look
    /// entirely plausible.
    /// </summary>
    [Fact]
    public void Compose_Always_RendersTheTimesInTheBookersOwnZone()
    {
        var message = Compose();
        var subject = message.Subject!;

        subject.ShouldContain("09:00");
        subject.ShouldContain(BookerZone);
        PlainPart(message).Text.ShouldContain("09:00-09:25");
    }

    /// <summary>
    /// This deployment's state today, and the one that must never throw: no relay configured at
    /// all. The mailer is called on the booking's own request, so anything it raises here is a
    /// booking that answers 500 because nobody has set up SMTP yet.
    /// </summary>
    [Fact]
    public async Task SendConfirmationAsync_NoRelayConfigured_DoesNothingAndDoesNotThrow() =>
        await Should.NotThrowAsync(() => Mailer(new EmailOptions())
            .SendConfirmationAsync(Booking(), ManageToken, TestContext.Current.CancellationToken));

    /// <summary>
    /// Ground rule 14, against a real socket. A relay that is configured and refuses the
    /// connection is the failure the outbox would have absorbed and this design does not — the
    /// invitee gets nothing, the booking stands, and the caller is never told. Reached with a
    /// real <see cref="EmailSender"/> pointed at a closed port rather than with a mock.
    /// </summary>
    [Fact]
    public async Task SendConfirmationAsync_RelayRefusesTheConnection_SwallowsItSoTheBookingStands()
    {
        var unreachable = Options(BaseUrl);
        unreachable.Host = "127.0.0.1";
        unreachable.Port = PortNothingListensOn;

        await Should.NotThrowAsync(() => Mailer(unreachable)
            .SendConfirmationAsync(Booking(), ManageToken, TestContext.Current.CancellationToken));
    }

    private static EmailOptions Options(string baseUrl, string from = OrganizerAddress) =>
        new()
        {
            Host = "smtp.example.com",
            Username = "kairos@example.com",
            Password = "not-a-real-password",
            From = from,
            PublicBaseUrl = baseUrl,
        };

    private static BookingMailer Mailer(EmailOptions options)
    {
        var wrapped = Microsoft.Extensions.Options.Options.Create(options);
        return new BookingMailer(
            new EmailSender(wrapped),
            new IcsBuilder(),
            wrapped,
            NullLogger<BookingMailer>.Instance);
    }

    private static Booking Booking(int sequence = 0)
    {
        var id = Guid.CreateVersion7();
        return new Booking
        {
            Id = id,
            Sequence = sequence,
            StartUtc = Start.ToUnixTimeSeconds(),
            EndUtc = (Start + Duration.FromMinutes(DurationMinutes)).ToUnixTimeSeconds(),
            Status = BookingStatus.Confirmed,
            ManageTokenHash = string.Empty,
            Notes = string.Empty,
            Link = new Link { Slug = "consulting", Name = LinkName },
            Participants =
            [
                new BookingParticipant
                {
                    Id = Guid.CreateVersion7(),
                    BookingId = id,
                    Role = ParticipantRole.Booker,
                    Name = BookerName,
                    Email = BookerAddress,
                    TimeZoneId = BookerZone,
                },
            ],
        };
    }

    /// <summary>
    /// Composed, written and parsed back. Reading the object the mailer returned would skip the
    /// transfer encodings, which is precisely where the .ics survives or does not.
    /// </summary>
    private static MimeMessage Compose(
        Booking? booking = null,
        EmailOptions? options = null,
        BookingNotice notice = BookingNotice.Confirmation,
        string? manageToken = ManageToken)
    {
        using var stream = new MemoryStream();
        Mailer(options ?? Options(BaseUrl))
            .Compose(booking ?? Booking(), manageToken, notice)
            .WriteTo(stream);
        stream.Position = 0;
        return MimeMessage.Load(stream);
    }

    /// <summary>
    /// The calendar part's bytes as they were transmitted. Deliberately not <c>TextPart.Text</c>:
    /// that getter normalises line endings to the platform's, which silently turns the CRLF this
    /// feature depends on into LF and would let a broken .ics pass every assertion here.
    /// </summary>
    private static string Ics(MimeMessage message) => Read(CalendarPart(message));

    private static TextPart CalendarPart(MimeMessage message) =>
        message.BodyParts.OfType<TextPart>()
            .Single(part => part.ContentType.IsMimeType("text", "calendar"));

    /// <summary>
    /// The plain body with its line endings pinned to one spelling. <see cref="TextPart.Text"/>
    /// has already normalised them to whatever the running platform uses, which is fine for an
    /// assertion about a URL and not fine for one about blank lines.
    /// </summary>
    private static string Plain(MimeMessage message) =>
        PlainPart(message).Text.ReplaceLineEndings("\n");

    private static TextPart PlainPart(MimeMessage message) =>
        message.BodyParts.OfType<TextPart>()
            .Single(part => part.ContentType.IsMimeType("text", "plain"));

    private static string Read(MimePart part)
    {
        using var stream = new MemoryStream();
        part.Content!.DecodeTo(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>A folded line means nothing until it is joined back up, which is what a client does.</summary>
    private static string Unfold(string ics) =>
        ics.Replace("\r\n ", string.Empty, StringComparison.Ordinal);
}
