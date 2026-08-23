using MimeKit;
using MimeKit.Utils;

using NodaTime;
using NodaTime.Text;

namespace Kairos.Api.Integrations.Email;

/// <remarks>
/// One home rather than a copy per mailer, and the Message-Id earns it a type of its own. MimeKit
/// otherwise derives that header's right-hand side from the machine's hostname, which in a
/// container is a random hex string and not a resolvable domain; filters read that as a forgery
/// signal. One easy line to leave out, and a mailer written without it fails in the one way
/// nothing here can assert: the message sends, the relay accepts it, and it lands in spam.
///
/// The date and time formats are here for a smaller but real reason: an invitee reading "Monday 7
/// September 2026" in one message and a different rendering in the next has to work out whether
/// they are being told about the same meeting.
/// </remarks>
public static class OutgoingMail
{
    private static readonly ZonedDateTimePattern DatePattern =
        ZonedDateTimePattern.CreateWithInvariantCulture("dddd d MMMM yyyy", DateTimeZoneProviders.Tzdb);

    private static readonly ZonedDateTimePattern TimePattern =
        ZonedDateTimePattern.CreateWithInvariantCulture("HH:mm", DateTimeZoneProviders.Tzdb);

    /// <summary>The day, spelled out: <c>Monday 7 September 2026</c>.</summary>
    public static string Date(ZonedDateTime value) => DatePattern.Format(value);

    /// <summary>The hour, 24 clock and no seconds. Never written without its zone beside it.</summary>
    public static string Time(ZonedDateTime value) => TimePattern.Format(value);

    /// <param name="recipient">
    /// A parameter rather than derived, because the mailers disagree about who that is: the
    /// invitee for anything about their meeting, and the organizer's own mailbox for anything
    /// about the suggestions they made.
    /// </param>
    public static MimeMessage Envelope(
        string subject,
        MimeEntity body,
        MailboxAddress organizer,
        MailboxAddress recipient)
    {
        var message = new MimeMessage
        {
            Subject = subject,
            Body = body,
        };

        message.From.Add(organizer);
        message.To.Add(recipient);
        message.MessageId = MimeUtils.GenerateMessageId(organizer.Domain);

        return message;
    }

    /// <summary>
    /// A path on this deployment. The base is trimmed so a <see cref="EmailOptions.PublicBaseUrl"/>
    /// written with a trailing slash does not produce a double one that some mail clients decline
    /// to linkify.
    /// </summary>
    /// <param name="path">
    /// Joined as given and NOT escaped, because a caller building a multi-segment path is the only
    /// one that knows which segments are data. Both callers today escape what they interpolate; a
    /// third that does not is how a token with a slash in it becomes two segments.
    /// </param>
    public static string Url(string baseUrl, string path) => $"{baseUrl.TrimEnd('/')}/{path}";
}
