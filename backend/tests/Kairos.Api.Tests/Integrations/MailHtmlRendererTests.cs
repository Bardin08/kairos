using Kairos.Api.Integrations.Email;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Shouldly;

namespace Kairos.Api.Tests.Integrations;

/// <summary>
/// The HTML half. What it must contain, and — more usefully — what it must never contain.
/// </summary>
public class MailHtmlRendererTests
{
    private const string Headline = "You are booked";
    private const string Sentence = "Your time with Vladyslav is confirmed.";
    private const string Time = "14:00 - 14:30";
    private const string Date = "Monday 7 September 2026";
    private const string Zone = "Europe/Warsaw";
    private const string ActionLabel = "Accept";
    private const string ActionUrl = "https://kairos.test/manage/2f9c";
    private const string SecondaryLabel = "Decline";
    private const string ReasonText = "Something urgent came up.";
    private const string ReasonHeading = "Why";

    /// <summary>The mark's tile and the action. Nothing else may be painted in the accent.</summary>
    private const int AccentedElements = 2;

    /// <summary>The mark's four cells less the chosen one, drawn at the size a mail header allows.</summary>
    private const int UnchosenDays = 3;

    /// <summary>The four ways a message asks a mail client to go and fetch something.</summary>
    private const string Image = "<img";
    private const string FontFace = "@font-face";
    private const string Import = "@import";
    private const string BackgroundImage = "background-image";

    [Fact]
    public async Task RenderAsync_PutsTheHeadlineAndTheFactInTheOutput()
    {
        var html = await Render(Minimal());

        html.ShouldContain(Headline);
        html.ShouldContain(Time);
    }

    /// <summary>
    /// Identity survives image blocking or it is not identity. No remote image, no tracking
    /// pixel, no icon font, no web font — asserted here as well as in the invariant suite,
    /// because this is the file where one would get added. Shouldly compares case-insensitively,
    /// so an uppercase tag does not slip past.
    /// </summary>
    [Fact]
    public async Task RenderAsync_RequestsNoRemoteAsset()
    {
        var html = await Render(Minimal());

        html.ShouldNotContain(Image);
        html.ShouldNotContain(FontFace);
        html.ShouldNotContain(Import);
        html.ShouldNotContain(BackgroundImage);
    }

    /// <summary>
    /// The header draws the four-cell mark out of table cells: three days and the chosen one.
    /// Counted by the days' own colour, which nothing else in a message wears.
    /// </summary>
    [Fact]
    public async Task RenderAsync_DrawsTheMarkAsThreeDaysAndTheChosenOne()
    {
        var html = await Render(Minimal());

        var days = html.Split(MailPalette.Light.MarkDay).Length - 1;

        days.ShouldBe(UnchosenDays);
    }

    [Fact]
    public async Task RenderAsync_NoReason_WritesNoHeadingOverNothing()
    {
        (await Render(Minimal())).ShouldNotContain(ReasonHeading);
    }

    [Fact]
    public async Task RenderAsync_AReason_PrintsIt()
    {
        var html = await Render(Minimal() with { Reason = ReasonText });

        html.ShouldContain(ReasonHeading);
        html.ShouldContain(ReasonText);
    }

    /// <summary>
    /// The action is the accent's second and last job. One accented control per message: a second
    /// would be a third job, which the design system's Two Jobs Rule does not have.
    /// </summary>
    [Fact]
    public async Task RenderAsync_AnActionAndASecondary_AccentsOnlyTheAction()
    {
        var body = Minimal() with
        {
            Action = new MailAction(ActionLabel, ActionUrl),
            Secondary = new MailSecondary(SecondaryLabel, ActionUrl),
        };

        var occurrences = (await Render(body)).Split(MailPalette.Light.Accent).Length - 1;

        occurrences.ShouldBe(AccentedElements);
    }

    private static async Task<string> Render(MailBody body)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var renderer = new MailHtmlRenderer(
            services, services.GetRequiredService<ILoggerFactory>());

        return await renderer.RenderAsync(body, TestContext.Current.CancellationToken);
    }

    private static MailBody Minimal() => new()
    {
        Headline = Headline,
        Fact = new MailFact(Time, Date, Zone, null),
        Sentence = Sentence,
    };
}
