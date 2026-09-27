using Kairos.Api.Integrations.Email;

using Shouldly;

namespace Kairos.Api.Tests.Integrations;

/// <summary>
/// The one rule about this palette that is a correctness claim rather than taste.
/// </summary>
public class MailPaletteTests
{
    private const string White = "#FFFFFF";
    private const string HexPrefix = "#";

    /// <summary>A hex colour written the only way every mail client parses: <c>#</c> and six digits.</summary>
    private const int HexLength = 7;

    private const int ChannelCount = 3;
    private const int ChannelDigits = 2;
    private const int HexBase = 16;
    private const string ChannelFormat = "X2";

    /// <summary>A 50% mix: the two channels summed, then halved.</summary>
    private const int Halves = 2;

    /// <summary>
    /// The Inversion Rule from docs/ui/DESIGN.md: the dark-mode accent lightens, so what sits on
    /// it must darken. White on it measures ~3.5:1 and is prohibited. Asserted rather than trusted
    /// because it is one hex value away from being wrong and nothing else would notice.
    /// </summary>
    [Fact]
    public void Dark_PutsNoWhiteOnTheAccent()
    {
        MailPalette.Dark.OnAccent.ToUpperInvariant().ShouldNotBe(White);
    }

    /// <summary>
    /// The mark's unchosen days are the chosen disc's colour at half strength over the tile,
    /// premixed because a mail client cannot be trusted with opacity. Asserted because the dark
    /// value has to follow the Inversion Rule too, and a hand-typed midpoint drifts unnoticed.
    /// </summary>
    [Fact]
    public void Light_AndDark_MixTheMarkDayHalfwayFromTheAccentToWhatSitsOnIt()
    {
        foreach (var palette in new[] { MailPalette.Light, MailPalette.Dark })
        {
            palette.MarkDay.ShouldBe(Midpoint(palette.OnAccent, palette.Accent), StringCompareShould.IgnoreCase);
        }
    }

    [Fact]
    public void Light_AndDark_DefineEverySlot()
    {
        foreach (var palette in new[] { MailPalette.Light, MailPalette.Dark })
        {
            foreach (var slot in palette.Slots())
            {
                slot.ShouldStartWith(HexPrefix);
                slot.Length.ShouldBe(HexLength);
            }
        }
    }

    /// <summary>
    /// Channel by channel, truncated: the values the brand assets in docs/brand were drawn with.
    /// </summary>
    private static string Midpoint(string first, string second)
    {
        var mixed = Channels(first).Zip(Channels(second), (a, b) => (a + b) / Halves);

        return HexPrefix + string.Concat(mixed.Select(channel => channel.ToString(ChannelFormat)));
    }

    private static IEnumerable<int> Channels(string hex) =>
        Enumerable.Range(0, ChannelCount)
            .Select(index => Convert.ToInt32(hex.Substring(HexPrefix.Length + (index * ChannelDigits), ChannelDigits), HexBase));
}
