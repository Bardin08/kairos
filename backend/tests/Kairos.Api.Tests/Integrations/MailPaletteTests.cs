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
}
