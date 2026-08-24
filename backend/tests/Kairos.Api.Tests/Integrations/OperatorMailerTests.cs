using Kairos.Api.Integrations.Email;
using Kairos.Api.Persistence.Entities;

using Microsoft.Extensions.Logging.Abstractions;

using MimeKit;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Integrations;

/// <summary>
/// What the organizer is told about a suggestion nobody acted on, asserted on the bytes that go
/// on the wire exactly as <see cref="BookingMailerTests"/> asserts the invitee's mail.
/// </summary>
/// <remarks>
/// Nothing here mocks SMTP, for the reason that file gives: the one test that reaches the network
/// points a real <see cref="EmailSender"/> at a port nothing listens on.
/// </remarks>
public class OperatorMailerTests
{
    private const string FromAddress = "organizer@example.com";
    private const string BookerAddress = "guest@example.com";
    private const string BookerName = "Ada Lovelace";
    private const string LinkName = "Consulting call";

    /// <summary>
    /// The invitee's zone, and deliberately not the one this mail renders in: every time is shown
    /// in the zone of whoever it is addressed to. A composer reaching for the booker's would
    /// render 09:00 and 13:00.
    /// </summary>
    private const string BookerZone = "Europe/Warsaw";

    private const string OrganizerZone = "Asia/Tokyo";
    private const string BaseUrl = "https://kairos.example.com";
    private const int DurationMinutes = 25;

    /// <summary>
    /// Reserved by IANA and bindable by nothing on any platform, so a connection to it is refused
    /// at once rather than after a timeout — the same handle <see cref="BookingMailerTests"/> uses
    /// to reach the failure path without a relay and without a mock.
    /// </summary>
    private const int PortNothingListensOn = 1;

    private const string DeclinedHeadline = $"{BookerName} declined the new time for {LinkName}.";
    private const string ExpiredHeadline = $"{BookerName} did not answer the new time for {LinkName}.";
    private const string StandsWhereItWas = "The meeting stays where it is:";
    private const string SinceCancelled = "The meeting has since been cancelled";

    /// <summary>The proposal's hour in Tokyo. 11:00Z is 20:00 there and 13:00 in Warsaw.</summary>
    private const string ProposedInTokyo = "20:00";

    /// <summary>And the meeting's own. 07:00Z is 16:00 in Tokyo and 09:00 in Warsaw.</summary>
    private const string CurrentInTokyo = "16:00";

    private static readonly Duration MeetingLength = Duration.FromMinutes(DurationMinutes);

    private static readonly Instant Start = Instant.FromUtc(2026, 9, 7, 7, 0);

    /// <summary>A day after the meeting, at an hour Tokyo renders differently from Warsaw.</summary>
    private static readonly Instant ProposedStart = Instant.FromUtc(2026, 9, 8, 11, 0);

    /// <summary>After the meeting and before the hour it offers, the only window an expiry fits.</summary>
    private static readonly Instant ProposalExpiry = Instant.FromUtc(2026, 9, 7, 17, 0);

    /// <summary>
    /// Nothing about a suggestion nobody acted on changes a calendar, hence no calendar part. The
    /// address is the one Kairos sends from, the only operator mailbox it has.
    /// </summary>
    [Fact]
    public void Compose_ADeclinedProposal_GoesToTheSendingAddressWithNoCalendarPart()
    {
        var message = Compose(OperatorNotice.Declined);

        message.To.Mailboxes.Single().Address.ShouldBe(FromAddress);
        message.BodyParts.OfType<TextPart>().ShouldHaveSingleItem();
        message.Attachments.ShouldBeEmpty();
    }

    /// <summary>
    /// The operator is handed the one screen that lets them suggest another hour, and no token
    /// anywhere in it — the link the invitee held is spent.
    /// </summary>
    [Fact]
    public void Compose_ADeclinedProposal_SaysSoAndPointsAtTheWorkbench()
    {
        var body = Plain(Compose(OperatorNotice.Declined));

        body.ShouldContain(DeclinedHeadline);
        body.ShouldContain($"{BaseUrl}/admin/meetings");
    }

    /// <summary>
    /// The difference the enum exists for: nobody answered is not the same news as somebody said
    /// no. Both end on the sentence that makes the mail actionable — the meeting has not moved.
    /// </summary>
    [Fact]
    public void Compose_AnExpiredProposal_SaysNobodyAnsweredAndTheMeetingStandsWhereItWas()
    {
        var body = Plain(Compose(OperatorNotice.Expired));

        body.ShouldContain(ExpiredHeadline);
        body.ShouldContain(StandsWhereItWas);
    }

    /// <summary>
    /// The one sentence in this mail that can go stale under it. A cancel closes an OPEN hold, so
    /// a row already Expired survives its meeting being called off — and the next tick to pick it
    /// up would otherwise tell the operator a meeting they cancelled stays where it is.
    /// </summary>
    [Fact]
    public void Compose_AProposalOnAMeetingSinceCancelled_DoesNotSayItStandsWhereItWas()
    {
        var body = Plain(Compose(OperatorNotice.Expired, BookingStatus.Cancelled));

        body.ShouldNotContain(StandsWhereItWas);
        body.ShouldContain(SinceCancelled);
    }

    /// <summary>
    /// Both instants are asserted, because the hour that was suggested and the hour the meeting is
    /// still at are read off the same line of a calendar, and one of them rendered in a stranger's
    /// zone is a missed call.
    /// </summary>
    [Fact]
    public void Compose_Always_RendersBothTimesInTheOrganizersOwnZone()
    {
        var body = Plain(Compose(OperatorNotice.Expired));

        body.ShouldContain(ProposedInTokyo);
        body.ShouldContain(CurrentInTokyo);
        body.ShouldContain(OrganizerZone);
    }

    /// <summary>
    /// A fresh deployment's state, and the one that must never throw. It answers true, which is
    /// not a claim that a message went: there was never going to be one, so the caller has nothing
    /// left to do. A false here would have the sweeper re-reading the same rows every minute for
    /// the life of a deployment that has no relay.
    /// </summary>
    [Fact]
    public async Task SendAsync_NoRelayConfigured_ReportsNothingLeftToDo() =>
        (await Mailer(new EmailOptions()).SendAsync(
            Booking(),
            Proposal(),
            OperatorNotice.Expired,
            OrganizerZone,
            TestContext.Current.CancellationToken)).ShouldBeTrue();

    /// <summary>
    /// Ground rule 14 against a real socket, and the answer that makes the sweeper's guarantee
    /// reach past the relay. The refusal is swallowed — nothing upstream fails — and it is
    /// reported, so the caller can decline to record a notice that was never delivered.
    /// </summary>
    [Fact]
    public async Task SendAsync_RelayRefusesTheConnection_SwallowsItAndSaysTheNoticeDidNotGo()
    {
        var unreachable = Options();
        unreachable.Host = "127.0.0.1";
        unreachable.Port = PortNothingListensOn;

        (await Mailer(unreachable).SendAsync(
            Booking(),
            Proposal(),
            OperatorNotice.Declined,
            OrganizerZone,
            TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    /// <summary>Composed, written and parsed back, so what is read is what a relay would hand on.</summary>
    private static MimeMessage Compose(
        OperatorNotice notice,
        BookingStatus status = BookingStatus.Confirmed)
    {
        using var stream = new MemoryStream();
        Mailer(Options()).Compose(Booking(status), Proposal(), notice, OrganizerZone).WriteTo(stream);
        stream.Position = 0;
        return MimeMessage.Load(stream);
    }

    private static string Plain(MimeMessage message) =>
        message.BodyParts.OfType<TextPart>().Single().Text;

    private static EmailOptions Options() =>
        new()
        {
            Host = "smtp.example.com",
            Username = "kairos@example.com",
            Password = "not-a-real-password",
            From = FromAddress,
            PublicBaseUrl = BaseUrl,
        };

    private static OperatorMailer Mailer(EmailOptions options)
    {
        var wrapped = Microsoft.Extensions.Options.Options.Create(options);
        return new OperatorMailer(new EmailSender(wrapped), wrapped, NullLogger<OperatorMailer>.Instance);
    }

    private static Booking Booking(BookingStatus status = BookingStatus.Confirmed)
    {
        var id = Guid.CreateVersion7();
        return new Booking
        {
            Id = id,
            StartUtc = Start.ToUnixTimeSeconds(),
            EndUtc = (Start + MeetingLength).ToUnixTimeSeconds(),
            Status = status,
            ManageTokenHash = string.Empty,
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

    /// <summary>A proposal whose token is never read: this mailer is the one that carries none.</summary>
    private static BookingProposal Proposal() =>
        new()
        {
            Id = Guid.CreateVersion7(),
            StartUtc = ProposedStart.ToUnixTimeSeconds(),
            EndUtc = (ProposedStart + MeetingLength).ToUnixTimeSeconds(),
            ExpiresAt = ProposalExpiry.ToUnixTimeSeconds(),
            TokenHash = string.Empty,
        };
}
