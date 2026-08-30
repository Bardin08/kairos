using System.Text;

using NodaTime;
using NodaTime.Text;

namespace Kairos.Api.Integrations.Email;

/// <summary>
/// Renders one booking as an RFC 5545 iCalendar object: a single VEVENT, no recurrence,
/// every time in UTC.
/// </summary>
/// <remarks>
/// Hand-rolled rather than delegated to Ical.Net: the four rules that matter — CRLF endings, TEXT
/// escaping, folding at 75 octets and the UTC DATE-TIME format — are each a few lines and each
/// directly tested. The email body and the Gmail/Outlook verification downstream pin this exact
/// text, so property order must not be a library's to change.
/// </remarks>
public sealed class IcsBuilder(IClock? clock = null)
{
    /// <summary>RFC 5545 §3.1: a content line is at most 75 octets before its line break.</summary>
    private const int MaxLineOctets = 75;

    /// <summary>A folded continuation spends one of its octets on the leading space.</summary>
    private const int MaxContinuationOctets = MaxLineOctets - 1;

    /// <summary>UTF-8 continuation octets are 10xxxxxx; a fold must never land on one.</summary>
    private const byte ContinuationMask = 0xC0;
    private const byte ContinuationMarker = 0x80;

    private const string LineBreak = "\r\n";
    private const string ProductId = "-//Kairos//Kairos Scheduling//EN";
    private const string LocationProperty = "LOCATION";

    private static readonly InstantPattern UtcPattern =
        InstantPattern.CreateWithInvariantCulture("uuuuMMdd'T'HHmmss'Z'");

    private readonly IClock _clock = clock ?? SystemClock.Instance;

    public string Build(IcsRequest request)
    {
        string[] lines =
        [
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            $"PRODID:{ProductId}",
            "CALSCALE:GREGORIAN",
            $"METHOD:{Keyword(request.Method)}",
            "BEGIN:VEVENT",
            $"UID:{request.Uid}",
            $"DTSTAMP:{UtcPattern.Format(_clock.GetCurrentInstant())}",
            $"DTSTART:{UtcPattern.Format(request.Start)}",
            $"DTEND:{UtcPattern.Format(request.End)}",
            $"SUMMARY:{Escape(request.Summary)}",
            $"DESCRIPTION:{Escape(request.Description)}",

            // Omitted rather than written empty: a client renders an empty LOCATION as a blank
            // location field, which says the place is unknown rather than that there is none.
            .. request.Location is { Length: > 0 } location
                ? (string[])[$"{LocationProperty}:{Escape(location)}"]
                : [],

            $"ORGANIZER{SentBy(request.SentBy)}:mailto:{request.OrganizerEmail}",
            $"ATTENDEE;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:{request.AttendeeEmail}",
            $"SEQUENCE:{request.Sequence}",
            $"STATUS:{Status(request.Method)}",
            "END:VEVENT",
            "END:VCALENDAR",
        ];

        var ics = new StringBuilder();
        foreach (var line in lines)
        {
            ics.Append(Fold(line)).Append(LineBreak);
        }

        return ics.ToString();
    }

    /// <summary>
    /// The METHOD keyword this object carries, which the MIME <c>method=</c> parameter around it
    /// has to spell too. Public so the two are one value: a part labelled REQUEST wrapping a
    /// CANCEL is discarded by clients in silence, like a wrong SEQUENCE.
    /// </summary>
    public static string Keyword(IcsMethod method) => method switch
    {
        IcsMethod.Request => "REQUEST",
        IcsMethod.Cancel => "CANCEL",
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown iCalendar method."),
    };

    /// <summary>
    /// RFC 5545 §3.2.18. Quoted because the value holds a colon, and omitted rather than written
    /// empty: an organizer that posts for itself has nothing to say here.
    /// </summary>
    private static string SentBy(string? sentBy) =>
        sentBy is { Length: > 0 } address ? $";SENT-BY=\"mailto:{address}\"" : string.Empty;

    /// <summary>
    /// METHOD:CANCEL alone is not enough — Outlook removes the event on STATUS:CANCELLED,
    /// and an event left CONFIRMED stays in the calendar however the message was labelled.
    /// </summary>
    private static string Status(IcsMethod method) =>
        method == IcsMethod.Cancel ? "CANCELLED" : "CONFIRMED";

    /// <summary>
    /// RFC 5545 §3.3.11. A booking's notes are free text typed by a stranger, and one
    /// unescaped comma turns the rest of the property into a second value.
    /// </summary>
    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace("\r\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\n", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>RFC 5545 §3.1 line folding, counted in octets rather than characters.</summary>
    private static string Fold(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.Length <= MaxLineOctets)
        {
            return line;
        }

        var folded = new StringBuilder();
        var offset = 0;
        var limit = MaxLineOctets;

        while (offset < bytes.Length)
        {
            var take = Math.Min(limit, bytes.Length - offset);
            while ((take > 1) && ((offset + take) < bytes.Length) && IsContinuation(bytes[offset + take]))
            {
                take--;
            }

            if (offset > 0)
            {
                folded.Append(LineBreak).Append(' ');
            }

            folded.Append(Encoding.UTF8.GetString(bytes, offset, take));
            offset += take;
            limit = MaxContinuationOctets;
        }

        return folded.ToString();
    }

    private static bool IsContinuation(byte octet) => (octet & ContinuationMask) == ContinuationMarker;
}
