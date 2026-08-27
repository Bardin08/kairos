using System.Text.Json;

using Kairos.Api.Features.Invites;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The bag is untyped in the database and validated here, which makes this the only thing
/// standing between an operator's typo and a form that silently ignores a field.
/// </summary>
public class InvitePrefillTests
{
    private const string Name = "name";
    private const string Email = "email";
    private const string Notes = "notes";
    private const string UnknownField = "favourite_colour";
    private const string SomeName = "Ada";
    private const string SomeEmail = "ada@example.com";
    private const string SomeNotes = "About the migration";
    private const string SomeColour = "green";

    [Fact]
    public void Validate_AKeyTheBookingPayloadDoesNotAccept_IsRefused()
    {
        var prefill = new Dictionary<string, string> { [UnknownField] = SomeColour };

        var result = InvitePrefill.Validate(prefill);

        result.IsValid.ShouldBeFalse();
        result.UnknownKey.ShouldBe(UnknownField);
    }

    [Fact]
    public void Validate_TheThreeFieldsTheFormHasToday_IsAccepted()
    {
        var prefill = new Dictionary<string, string>
        {
            [Name] = SomeName,
            [Email] = SomeEmail,
            [Notes] = SomeNotes,
        };

        InvitePrefill.Validate(prefill).IsValid.ShouldBeTrue();
    }

    /// <summary>An operator who set nothing still gets a working single-use URL.</summary>
    [Fact]
    public void Validate_AnEmptyBag_IsAccepted()
    {
        InvitePrefill.Validate(new Dictionary<string, string>()).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Serialize_ThenRead_RoundTrips()
    {
        var prefill = new Dictionary<string, string> { [Name] = SomeName };

        var read = InvitePrefill.Read(InvitePrefill.Serialize(prefill));

        read[Name].ShouldBe(SomeName);
    }

    /// <summary>
    /// A row written by a future version carrying a key this one has retired. Reading has to
    /// survive it: the invite is still spendable, and a form that cannot seed one field is not a
    /// reason to refuse a booking.
    /// </summary>
    [Fact]
    public void Read_AKeyThisVersionDoesNotKnow_IsKeptRatherThanThrowing()
    {
        var stored = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [UnknownField] = SomeColour,
        });

        InvitePrefill.Read(stored).ShouldContainKey(UnknownField);
    }
}
