using System.Text;

using Kairos.Api.Integrations.Email;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Integrations;

public class IcsBuilderTests
{
    private static readonly Guid BookingId = Guid.Parse("0199a1b2-c3d4-7e5f-8091-a2b3c4d5e6f7");
    private const int NonRoundDurationMinutes = 25;

    /// <summary>RFC 5545 §3.1 — a content line SHOULD NOT exceed this many octets, line break excluded.</summary>
    private const int MaxLineOctets = 75;

    private const string LineBreak = "\r\n";

    private const string LocationProperty = "LOCATION";
    private const string JoinUrl = "https://meet.example/abc-defg-hij";

    private static IcsRequest Request(
        int sequence = 0,
        IcsMethod method = IcsMethod.Request,
        string summary = "Consulting",
        string description = "",
        string? location = null) =>
        new(
            BookingId: BookingId,
            Start: Instant.FromUtc(2026, 9, 7, 9, 0),
            End: Instant.FromUtc(2026, 9, 7, 9, NonRoundDurationMinutes),
            Summary: summary,
            Description: description,
            OrganizerEmail: "organizer@example.com",
            AttendeeEmail: "guest@example.com",
            Sequence: sequence,
            Method: method,
            Location: location);

    private static string Build(int sequence, IcsMethod method) =>
        new IcsBuilder().Build(Request(sequence, method));

    /// <summary>A folded line means nothing until it is joined back up, which is what a client does.</summary>
    private static string Unfold(string ics) => ics.Replace(LineBreak + " ", string.Empty, StringComparison.Ordinal);

    [Fact]
    public void Build_Always_UsesTheBookingIdAsUid() =>
        Build(0, IcsMethod.Request).ShouldContain($"UID:{BookingId}");

    [Fact]
    public void Build_NewInvitation_IsRequestAtSequenceZero()
    {
        var ics = Build(0, IcsMethod.Request);

        ics.ShouldContain("METHOD:REQUEST");
        ics.ShouldContain("SEQUENCE:0");
    }

    [Fact]
    public void Build_Reschedule_KeepsTheUidAndIncrementsTheSequence()
    {
        var ics = Build(1, IcsMethod.Request);

        ics.ShouldContain($"UID:{BookingId}");
        ics.ShouldContain("SEQUENCE:1");
    }

    [Fact]
    public void Build_Cancellation_UsesMethodCancel() =>
        Build(2, IcsMethod.Cancel).ShouldContain("METHOD:CANCEL");

    [Fact]
    public void Build_NonRoundDuration_SurvivesIntact() =>
        // Durations are free values, so DTEND lands off the half hour more often than not.
        Build(0, IcsMethod.Request).ShouldContain("DTEND:20260907T092500Z");

    [Fact]
    public void Build_Cancellation_MarksTheEventCancelled() =>
        // METHOD:CANCEL labels the message; STATUS:CANCELLED is what takes the event
        // out of the calendar — RFC 5546 §3.2.5 requires it to cancel the whole event.
        Build(2, IcsMethod.Cancel).ShouldContain("STATUS:CANCELLED");

    [Fact]
    public void Build_Always_StampsTheCurrentTimeInUtc() =>
        // RFC 5545 §3.6.1 makes DTSTAMP REQUIRED in every VEVENT.
        new IcsBuilder(new FixedClock(Instant.FromUtc(2026, 8, 15, 10, 15, 30)))
            .Build(Request())
            .ShouldContain("DTSTAMP:20260815T101530Z");

    [Fact]
    public void Build_Always_NamesBothParticipants()
    {
        // No ORGANIZER and no ATTENDEE means no accept/decline control in the client.
        var ics = Unfold(Build(0, IcsMethod.Request));

        ics.ShouldContain("ORGANIZER:mailto:organizer@example.com");
        ics.ShouldContain("ATTENDEE;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:guest@example.com");
    }

    [Theory]
    [InlineData("x")]
    [InlineData("ł")] // Two octets, so a fold counted in characters splits it and produces mojibake.
    public void Build_SummaryLongerThanOneLine_FoldsWithoutLosingAnything(string filler)
    {
        var summary = string.Concat(Enumerable.Repeat(filler, MaxLineOctets * 2));

        var ics = new IcsBuilder().Build(Request(summary: summary));

        foreach (var line in ics.Split(LineBreak, StringSplitOptions.RemoveEmptyEntries))
        {
            Encoding.UTF8.GetByteCount(line).ShouldBeLessThanOrEqualTo(MaxLineOctets);
        }

        Unfold(ics).ShouldContain($"SUMMARY:{summary}");
    }

    [Fact]
    public void Build_DescriptionWithReservedCharacters_EscapesThem() =>
        // Notes are free text typed by a stranger; one raw comma corrupts the file from there on.
        Unfold(new IcsBuilder().Build(Request(description: "a,b;c\\d\ne")))
            .ShouldContain(@"DESCRIPTION:a\,b\;c\\d\ne");

    [Fact]
    public void Build_ABookingWithAConference_WritesItsUrlAsLocation() =>
        // LOCATION on the event is what makes Google Calendar and Outlook draw a join control,
        // rather than the invitee hunting for the URL in a mail they have to keep.
        new IcsBuilder().Build(Request(location: JoinUrl)).ShouldContain($"{LocationProperty}:{JoinUrl}");

    [Fact]
    public void Build_ABookingWithNoConference_WritesNoLocationPropertyAtAll() =>
        // Absent, not empty: a client renders an empty LOCATION as a blank location field, which
        // reads as a meeting whose place is unknown rather than one held wherever the two agreed.
        new IcsBuilder().Build(Request()).ShouldNotContain(LocationProperty);

    [Fact]
    public void Build_Always_EndsEveryLineWithCarriageReturnLineFeed()
    {
        // RFC 5545 requires CRLF. A bare LF is what Environment.NewLine hands you on Linux.
        var ics = Build(0, IcsMethod.Request);

        ics.ShouldEndWith(LineBreak);
        ics.Replace(LineBreak, string.Empty, StringComparison.Ordinal).ShouldNotContain("\n");
    }
}
