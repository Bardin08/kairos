namespace Kairos.Api.Integrations.Email;

/// <summary>
/// The colours one message is painted in, taken from <c>docs/ui/DESIGN.md</c>.
/// </summary>
/// <remarks>
/// Light is the baseline and is written inline on every element, because inline styles are the
/// only thing no mail client strips. Dark is an override in a style block under
/// prefers-color-scheme, which Apple Mail and Outlook.com honour, Gmail ignores in favour of its
/// own inversion, and some clients remove outright — so the light values have to survive being
/// inverted by somebody else's algorithm, and that is what the hand run checks.
/// </remarks>
public sealed record MailPalette(
    string Page,
    string Card,
    string Ink,
    string Muted,
    string Faint,
    string Line,
    string Hairline,
    string Accent,
    string OnAccent,
    string Wash,
    string MarkDay)
{
    public static readonly MailPalette Light = new(
        Page: "#F6F7F9",
        Card: "#FFFFFF",
        Ink: "#171A20",
        Muted: "#545A69",
        Faint: "#6E7484",
        Line: "#E1E3EA",
        Hairline: "#EDEEF2",
        Accent: "#4E56E3",
        OnAccent: "#FFFFFF",
        Wash: "#EEF0FE",
        MarkDay: "#A6AAF1");

    /// <summary>
    /// Not the light palette with its values flipped. The accent lightens so it holds against a
    /// near-black ground, and because it lightens, what sits on top of it darkens.
    /// </summary>
    public static readonly MailPalette Dark = new(
        Page: "#0E1014",
        Card: "#171A20",
        Ink: "#F3F4F7",
        Muted: "#A9AFBE",
        Faint: "#8A90A0",
        Line: "#2A2E38",
        Hairline: "#232730",
        Accent: "#737DEE",
        OnAccent: "#0B0D2B",
        Wash: "#1B1F35",
        MarkDay: "#3F458C");

    public IEnumerable<string> Slots() =>
        [Page, Card, Ink, Muted, Faint, Line, Hairline, Accent, OnAccent, Wash, MarkDay];
}
