using System.Text.RegularExpressions;

using Kairos.Api.Integrations.Email;
using Kairos.Api.Persistence.Entities;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using MimeKit;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Integrations;

/// <summary>
/// What every message must be true of, whatever it says.
/// </summary>
/// <remarks>
/// Driven by the enums rather than by a list written here, so a notice added later is covered the
/// day it is added rather than the day somebody remembers this file.
/// </remarks>
public class MailInvariantTests
{
    /// <summary>Gmail clips a message past this, and a clipped confirmation hides the cancel link.</summary>
    private const int GmailClipBytes = 102_400;

    private const string ManageToken = "3f2504e04f8911d39a0c0305e82c3301";
    private const string ProposalToken = "0f1e2d3c";
    private const string BaseUrl = "https://kairos.example.com";
    private const string JoinUrl = "https://meet.example.com/kairos-consulting";
    private const string FromAddress = "organizer@example.com";
    private const string BookerAddress = "guest@example.com";
    private const string BookerName = "Vlad";
    private const string LinkName = "Consulting call";
    private const string BookerZone = "Europe/Warsaw";
    private const string OrganizerZone = "Asia/Tokyo";
    private const string CancellationReason = "The client moved the workshop a week.";
    private const int DurationMinutes = 25;

    private const string TextMediaType = "text";
    private const string PlainSubtype = "plain";
    private const string HtmlSubtype = "html";
    private const string MultipartMediaType = "multipart";
    private const string AlternativeSubtype = "alternative";
    private const string AlternativeMimeType = $"{MultipartMediaType}/{AlternativeSubtype}";

    /// <summary>The four ways a message asks a mail client to go and fetch something.</summary>
    private const string Image = "<img";
    private const string FontFace = "@font-face";
    private const string Import = "@import";
    private const string BackgroundImage = "background-image";

    /// <summary>What every class the dark override keys on begins with.</summary>
    private const string DarkClassPrefix = "k-";

    private const string AttributeValue = "value";

    private static readonly Duration MeetingLength = Duration.FromMinutes(DurationMinutes);

    private static readonly Instant Start = Instant.FromUtc(2026, 9, 7, 7, 0);

    /// <summary>Where a rescheduled meeting moved from — the Friday before.</summary>
    private static readonly Instant PreviousStart = Instant.FromUtc(2026, 9, 4, 7, 0);

    private static readonly Instant ProposedStart = Instant.FromUtc(2026, 9, 8, 11, 0);

    /// <summary>After the meeting the proposal is about and before the time it offers.</summary>
    private static readonly Instant ProposalExpiry = Instant.FromUtc(2026, 9, 7, 17, 0);

    /// <summary>
    /// One opening tag, read whole so the class and the style attribute are seen together —
    /// neither says anything on its own, and their order in the markup is nobody's contract.
    /// </summary>
    private static readonly Regex OpeningTag = new("<[a-z][^>]*>", RegexOptions.IgnoreCase);

    private static readonly Regex StyleAttribute =
        new($"style=\"(?<{AttributeValue}>[^\"]*)\"", RegexOptions.IgnoreCase);

    private static readonly Regex ClassAttribute =
        new($"class=\"(?<{AttributeValue}>[^\"]*)\"", RegexOptions.IgnoreCase);

    public static TheoryData<BookingNotice> BookingNotices => [.. Enum.GetValues<BookingNotice>()];

    public static TheoryData<OperatorNotice> OperatorNotices => [.. Enum.GetValues<OperatorNotice>()];

    /// <summary>
    /// Both halves, every time. A client that prefers plaintext — a watch, a filter, a screen
    /// reader — reads the part nobody looks at while writing the other one.
    /// </summary>
    [Theory]
    [MemberData(nameof(BookingNotices))]
    public async Task ComposeAsync_EveryBookingNotice_IsAnAlternativeWithBothHalvesFilled(
        BookingNotice notice) =>
        ShouldCarryBothHalves(await ComposeBooking(notice));

    [Theory]
    [MemberData(nameof(OperatorNotices))]
    public async Task ComposeAsync_EveryOperatorNotice_IsAnAlternativeWithBothHalvesFilled(
        OperatorNotice notice) =>
        ShouldCarryBothHalves(await ComposeOperator(notice));

    /// <summary>
    /// Identity survives image blocking or it is not identity. No remote image, no tracking pixel,
    /// no icon font, no web font — in any message, not just the one whose renderer test says so.
    /// </summary>
    [Theory]
    [MemberData(nameof(BookingNotices))]
    public async Task ComposeAsync_EveryBookingNotice_RequestsNoRemoteAsset(BookingNotice notice) =>
        ShouldFetchNothing(Html(await ComposeBooking(notice)));

    [Theory]
    [MemberData(nameof(OperatorNotices))]
    public async Task ComposeAsync_EveryOperatorNotice_RequestsNoRemoteAsset(OperatorNotice notice) =>
        ShouldFetchNothing(Html(await ComposeOperator(notice)));

    /// <summary>
    /// Past the limit Gmail hides the tail of the message behind a "View entire message" link, and
    /// the tail is where the manage link and the footer are.
    /// </summary>
    [Theory]
    [MemberData(nameof(BookingNotices))]
    public async Task ComposeAsync_EveryBookingNotice_StaysUnderTheGmailClippingLimit(
        BookingNotice notice) =>
        Size(await ComposeBooking(notice)).ShouldBeLessThan(GmailClipBytes);

    [Theory]
    [MemberData(nameof(OperatorNotices))]
    public async Task ComposeAsync_EveryOperatorNotice_StaysUnderTheGmailClippingLimit(
        OperatorNotice notice) =>
        Size(await ComposeOperator(notice)).ShouldBeLessThan(GmailClipBytes);

    /// <summary>
    /// The invariant the renderer's own tests cannot hold, and the reason this file exists.
    /// </summary>
    /// <remarks>
    /// Light is inline because inline styles are the only thing no mail client strips; dark is a
    /// style block some clients honour and others remove outright. An element with the inline
    /// value and no class is invisible in dark mode, so every painted element needs both — and an
    /// assertion that the class appears SOMEWHERE in the document passes for a headline that lost
    /// it, which is exactly what the third task measured.
    /// </remarks>
    [Theory]
    [MemberData(nameof(BookingNotices))]
    public async Task ComposeAsync_EveryBookingNotice_GivesEveryPaintedElementItsDarkModeClass(
        BookingNotice notice) =>
        ShouldPaintNothingTheDarkOverrideCannotReach(Html(await ComposeBooking(notice)));

    [Theory]
    [MemberData(nameof(OperatorNotices))]
    public async Task ComposeAsync_EveryOperatorNotice_GivesEveryPaintedElementItsDarkModeClass(
        OperatorNotice notice) =>
        ShouldPaintNothingTheDarkOverrideCannotReach(Html(await ComposeOperator(notice)));

    private static void ShouldCarryBothHalves(MimeMessage message)
    {
        var alternative = Alternative(message);

        alternative.ContentType.MimeType.ShouldBe(AlternativeMimeType);
        Half(alternative, PlainSubtype).Text.ShouldNotBeNullOrWhiteSpace();
        Half(alternative, HtmlSubtype).Text.ShouldNotBeNullOrWhiteSpace();
    }

    private static void ShouldFetchNothing(string html)
    {
        html.ShouldNotContain(Image);
        html.ShouldNotContain(FontFace);
        html.ShouldNotContain(Import);
        html.ShouldNotContain(BackgroundImage);
    }

    /// <summary>
    /// Every element whose inline style carries a light value, and the class the dark block keys
    /// on that it must carry alongside it.
    /// </summary>
    /// <remarks>
    /// The style block is not a false positive and does not need skipping: its dark values sit in
    /// a text node rather than in a <c>style</c> attribute, so no opening tag holds them. That is
    /// worth having checked rather than assumed, because <c>Dark.Card</c> is byte-identical to
    /// <c>Light.Ink</c> — a scan reading the block's text would report the override itself as an
    /// element painted light with no class.
    /// </remarks>
    private static void ShouldPaintNothingTheDarkOverrideCannotReach(string html)
    {
        foreach (var tag in OpeningTag.Matches(html).Select(match => match.Value))
        {
            if (!IsPaintedLight(tag))
            {
                continue;
            }

            Attribute(ClassAttribute, tag).ShouldContain(
                DarkClassPrefix,
                Case.Sensitive,
                $"Painted in the light palette with no class the dark override can reach: {tag}");
        }
    }

    private static bool IsPaintedLight(string tag)
    {
        var style = Attribute(StyleAttribute, tag);

        return MailPalette.Light.Slots()
            .Any(slot => style.Contains(slot, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An attribute's value, or the empty string when the tag does not carry it at all.</summary>
    private static string Attribute(Regex attribute, string tag) =>
        attribute.Match(tag) is { Success: true } match ? match.Groups[AttributeValue].Value : string.Empty;

    /// <summary>
    /// The alternative, reached down the tree rather than off <see cref="MimeMessage.BodyParts"/>:
    /// that enumeration flattens the tree to its leaves, so the container asserted here is the one
    /// thing it never yields. Assignable rather than exact, because a written and parsed
    /// multipart/alternative comes back as <c>MultipartAlternative</c>.
    /// </summary>
    /// <remarks>
    /// Two shapes, because the calendar part decides the envelope: a notice carrying one is a
    /// multipart/mixed wrapping the alternative and the .ics attachment, and one carrying none is
    /// the alternative itself.
    /// </remarks>
    private static Multipart Alternative(MimeMessage message)
    {
        var body = message.Body.ShouldBeAssignableTo<Multipart>()!;

        return body.ContentType.IsMimeType(MultipartMediaType, AlternativeSubtype)
            ? body
            : body.OfType<Multipart>()
                .Single(part => part.ContentType.IsMimeType(MultipartMediaType, AlternativeSubtype));
    }

    private static TextPart Half(Multipart alternative, string subtype) =>
        alternative.OfType<TextPart>().Single(part => part.ContentType.IsMimeType(TextMediaType, subtype));

    private static string Html(MimeMessage message) =>
        message.BodyParts.OfType<TextPart>()
            .Single(part => part.ContentType.IsMimeType(TextMediaType, HtmlSubtype)).Text;

    /// <summary>What the relay is handed, headers and transfer encodings included.</summary>
    private static long Size(MimeMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return stream.Length;
    }

    /// <summary>
    /// The theories above are driven by <see cref="Enum.GetValues{TEnum}"/> through
    /// <see cref="BookingNotices"/>; this routing is not. A proposal and a reminder both carry no
    /// calendar part, so each composes elsewhere and <c>ComposeAsync</c> throws rather than build
    /// one for them — but that is this switch's own hand-written knowledge, and a
    /// <see cref="BookingNotice"/> added without a matching arm here falls into the default and
    /// throws for every theory above it, not just the ones that would actually catch something.
    /// </summary>
    private static async Task<MimeMessage> ComposeBooking(BookingNotice notice)
    {
        var mailer = Bookings();
        var booking = Booking();
        var message = notice switch
        {
            BookingNotice.Proposal => await mailer.ComposeProposalAsync(
                booking, Proposal(), ProposalToken, TestContext.Current.CancellationToken),
            BookingNotice.Confirmation or BookingNotice.Reschedule or BookingNotice.Cancellation =>
                await mailer.ComposeAsync(
                    booking,
                    new MeetingIdentity(booking.Id.ToString(), Owner: null),
                    ManageToken,
                    JoinUrl,
                    PreviousStart,
                    notice,
                    TestContext.Current.CancellationToken),
            BookingNotice.Reminder => await mailer.ComposeReminderAsync(
                booking, ManageToken, JoinUrl, TestContext.Current.CancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(notice), notice, "Unknown booking notice."),
        };

        return RoundTrip(message);
    }

    private static async Task<MimeMessage> ComposeOperator(OperatorNotice notice) =>
        RoundTrip(await Operators().ComposeAsync(
            Booking(), Proposal(), notice, OrganizerZone, TestContext.Current.CancellationToken));

    /// <summary>
    /// Composed, written and parsed back, exactly as <see cref="BookingMailerTests"/> does it: the
    /// object the mailer returned has not been through the transfer encodings, and the size and
    /// the markup asserted here are the ones a client receives.
    /// </summary>
    private static MimeMessage RoundTrip(MimeMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        stream.Position = 0;
        return MimeMessage.Load(stream);
    }

    private static IOptions<EmailOptions> Settings() =>
        Options.Create(new EmailOptions
        {
            Host = "smtp.example.com",
            Username = "kairos@example.com",
            Password = "not-a-real-password",
            From = FromAddress,
            PublicBaseUrl = BaseUrl,
        });

    private static BookingMailer Bookings()
    {
        var settings = Settings();
        return new BookingMailer(
            new EmailSender(settings),
            new IcsBuilder(),
            Renderer(),
            settings,
            NullLogger<BookingMailer>.Instance);
    }

    private static OperatorMailer Operators()
    {
        var settings = Settings();
        return new OperatorMailer(
            new EmailSender(settings), Renderer(), settings, NullLogger<OperatorMailer>.Instance);
    }

    private static MailHtmlRenderer Renderer()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        return new MailHtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
    }

    /// <summary>
    /// Carries a cancellation reason it would not always have, and is composed with a join URL and
    /// a previous start it would not always have either. Every optional block is switched on
    /// deliberately: an element the fixture never renders is an element the scan never reads.
    /// </summary>
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
            CancellationReason = CancellationReason,
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

    /// <summary>An open proposal. Its token hash is never read — nothing here resolves a token.</summary>
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
