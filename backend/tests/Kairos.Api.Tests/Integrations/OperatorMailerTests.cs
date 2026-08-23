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
    /// The invitee's zone, and deliberately not the organizer's: every time in this message is
    /// rendered in the zone of whoever it is addressed to, and this mail is addressed to the
    /// organizer. A composer that reached for the booker's zone would render 09:00 and 13:00.
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
    /// To the organizer, not to the invitee, and with no calendar part: nothing about a suggestion
    /// nobody acted on changes a calendar. The address is the one Kairos sends from, which is the
    /// only operator mailbox it has.
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
    /// The invitee said no, and the operator is handed the one screen that lets them suggest
    /// another hour. No token anywhere in it — the link the invitee held is spent.
    /// </summary>
    [Fact]
    public void Compose_ADeclinedProposal_SaysSoAndPointsAtTheWorkbench()
    {
        var body = Plain(Compose(OperatorNotice.Declined));

        body.ShouldContain(DeclinedHeadline);
        body.ShouldContain($"{BaseUrl}/admin/meetings");
    }

    /// <summary>
    /// The difference the enum exists for. Nobody answered is not the same news as somebody said
    /// no, and both end with the sentence that makes the mail actionable rather than alarming:
    /// the meeting has not moved.
    /// </summary>
    [Fact]
    public void Compose_AnExpiredProposal_SaysNobodyAnsweredAndTheMeetingStandsWhereItWas()
    {
        var body = Plain(Compose(OperatorNotice.Expired));

        body.ShouldContain(ExpiredHeadline);
        body.ShouldContain(StandsWhereItWas);
    }

    /// <summary>
    /// The organizer's zone, not the booker's, because the organizer is who this is addressed to
    /// — the same rule the invitee's mail follows in the other direction. Both instants are
    /// asserted: the hour that was suggested and the hour the meeting is still at are read off
    /// the same line of a calendar, and one of them rendered in a stranger's zone is a missed call.
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
    /// A fresh deployment's state, and the one that must never throw. The sweeper closes the
    /// proposal before it calls this, so anything raised here is a hold marked Expired by a tick
    /// that then died on the way out.
    /// </summary>
    [Fact]
    public async Task SendAsync_NoRelayConfigured_DoesNothingAndDoesNotThrow() =>
        await Should.NotThrowAsync(() => Mailer(new EmailOptions()).SendAsync(
            Booking(),
            Proposal(),
            OperatorNotice.Expired,
            OrganizerZone,
            TestContext.Current.CancellationToken));

    /// <summary>
    /// Ground rule 14 against a real socket. The proposal is already closed and the meeting is
    /// already where it belongs; a relay that refuses must cost the operator a message rather than
    /// leave a hold half-closed for the next tick to find.
    /// </summary>
    [Fact]
    public async Task SendAsync_RelayRefusesTheConnection_SwallowsItSoTheProposalStaysClosed()
    {
        var unreachable = Options();
        unreachable.Host = "127.0.0.1";
        unreachable.Port = PortNothingListensOn;

        await Should.NotThrowAsync(() => Mailer(unreachable).SendAsync(
            Booking(),
            Proposal(),
            OperatorNotice.Declined,
            OrganizerZone,
            TestContext.Current.CancellationToken));
    }

    /// <summary>Composed, written and parsed back, so what is read is what a relay would hand on.</summary>
    private static MimeMessage Compose(OperatorNotice notice)
    {
        using var stream = new MemoryStream();
        Mailer(Options()).Compose(Booking(), Proposal(), notice, OrganizerZone).WriteTo(stream);
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

    private static Booking Booking()
    {
        var id = Guid.CreateVersion7();
        return new Booking
        {
            Id = id,
            StartUtc = Start.ToUnixTimeSeconds(),
            EndUtc = (Start + MeetingLength).ToUnixTimeSeconds(),
            Status = BookingStatus.Confirmed,
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

    /// <summary>
    /// A proposal whose token is never looked at: this mailer is the one that carries none.
    /// </summary>
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
