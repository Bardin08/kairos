using Kairos.Api.Integrations.Email;

using Shouldly;

namespace Kairos.Api.Tests.Integrations;

/// <summary>
/// The half a filter reads, a screen reader reads, and the operator reads on a watch.
/// </summary>
/// <remarks>
/// The block rule is the whole subject. <c>BookingMailer.Body</c> assembled blocks joined by one
/// blank line rather than filling holes in a template, because two blocks are optional and a hole
/// leaves a mark — a stray blank line where a block was, a heading over nothing where its label
/// was. That construction survives the rewrite and these tests are what hold it in place.
/// </remarks>
public class MailTextRendererTests
{
    private const string Headline = "You are booked";
    private const string Sentence = "Your time with Vladyslav is confirmed.";
    private const string Time = "14:00 - 14:30";
    private const string Date = "Monday 7 September 2026";
    private const string Zone = "Europe/Warsaw";
    private const string ActionLabel = "Manage or cancel";
    private const string ActionUrl = "https://kairos.test/manage/2f9c";
    private const string ReasonText = "Something urgent came up.";
    private const string ReasonHeading = "Why:";
    private const string DetailLabel = "Duration";
    private const string DetailValue = "30 minutes";
    private const string ConferenceLabel = "Where";
    private const string ConferenceUrl = "https://meet.test/abc";
    private const string ConferenceValue = $"Google Meet\n{ConferenceUrl}";
    private const string UrlScheme = "http";
    private const string TwoBlankLines = "\n\n\n";

    /// <summary>The action's and the conference's, which is every URL the body below carries.</summary>
    private const int UrlLines = 2;

    [Fact]
    public void Render_TheFactBlock_CarriesTimeDateAndZoneTogether()
    {
        var text = MailTextRenderer.Render(Minimal());

        text.ShouldContain(Time);
        text.ShouldContain(Date);
        text.ShouldContain(Zone);
    }

    [Fact]
    public void Render_AnAction_PutsTheUrlOnItsOwnLineUnderItsLabel()
    {
        var body = Minimal() with { Action = new MailAction(ActionLabel, ActionUrl) };

        MailTextRenderer.Render(body).ShouldContain($"{ActionLabel}:\n{ActionUrl}");
    }

    /// <summary>The finding this renderer exists to prevent, in its most literal form.</summary>
    [Fact]
    public void Render_NoActionAndNoReason_LeavesNoBlankGapWhereTheyWouldHaveBeen()
    {
        MailTextRenderer.Render(Minimal()).ShouldNotContain(TwoBlankLines);
    }

    [Fact]
    public void Render_NoReason_WritesNoHeadingOverNothing()
    {
        MailTextRenderer.Render(Minimal()).ShouldNotContain(ReasonHeading);
    }

    [Fact]
    public void Render_AReason_PrintsItUnderItsHeading()
    {
        var body = Minimal() with { Reason = ReasonText };

        MailTextRenderer.Render(body).ShouldContain($"{ReasonHeading}\n{ReasonText}");
    }

    [Fact]
    public void Render_Details_WritesOneLabelledLineEach()
    {
        var body = Minimal() with { Details = [new MailDetail(DetailLabel, DetailValue)] };

        MailTextRenderer.Render(body).ShouldContain($"{DetailLabel}: {DetailValue}");
    }

    /// <summary>The linkify rule the renderer states, which nothing else in this file exercises.</summary>
    [Fact]
    public void Render_ADetailWithAUrl_PutsTheUrlOnItsOwnLine()
    {
        var body = Minimal() with { Details = [new MailDetail(ConferenceLabel, DetailValue, ConferenceUrl)] };

        MailTextRenderer.Render(body).ShouldContain($"{ConferenceLabel}: {DetailValue}\n{ConferenceUrl}");
    }

    [Fact]
    public void Render_NoDetails_WritesNoTrailingSeparator()
    {
        MailTextRenderer.Render(Minimal()).TrimEnd().ShouldBe(MailTextRenderer.Render(Minimal()));
    }

    /// <summary>Never a bare URL with nothing saying what pressing it does.</summary>
    [Fact]
    public void Render_EveryUrl_HasALabelledLineAboveIt()
    {
        var body = Minimal() with
        {
            Action = new MailAction(ActionLabel, ActionUrl),
            Details = [new MailDetail(ConferenceLabel, ConferenceValue)],
        };

        var lines = MailTextRenderer.Render(body).Split('\n');
        var found = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].StartsWith(UrlScheme, StringComparison.Ordinal))
            {
                continue;
            }

            found++;
            index.ShouldBeGreaterThan(0);
            lines[index - 1].ShouldNotBeNullOrWhiteSpace();
            lines[index - 1].ShouldNotStartWith(UrlScheme);
        }

        found.ShouldBe(UrlLines);
    }

    private static MailBody Minimal() => new()
    {
        Headline = Headline,
        Fact = new MailFact(Time, Date, Zone, null),
        Sentence = Sentence,
    };
}
