using System.Text.Json;

using Kairos.Api.Features.Bookings;
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

    /// <summary>What an over-long value is padded with. Only its count matters.</summary>
    private const char Filler = 'x';

    [Fact]
    public void Validate_AKeyTheBookingPayloadDoesNotAccept_IsRefused()
    {
        var prefill = new Dictionary<string, string> { [UnknownField] = SomeColour };

        var result = InvitePrefill.Validate(prefill);

        result.IsValid.ShouldBeFalse();
        result.Problem.ShouldBe(PrefillProblem.UnknownKey);
        result.OffendingKey.ShouldBe(UnknownField);
    }

    /// <summary>
    /// System.Text.Json ignores the nullable-reference annotation on the bag's value type, so this
    /// is a shape that really binds off the wire. Unrefused it stores as <c>{"name":null}</c>, is
    /// read back typed non-null, and lands in a controlled React input — after which the invitee's
    /// own booking POST fails <c>[Required]</c> with a 400 they have no way to fix.
    /// </summary>
    [Fact]
    public void Validate_AFillableKeyCarryingNull_IsRefusedAsAValueRatherThanAsTheKey()
    {
        var prefill = new Dictionary<string, string> { [Name] = null! };

        var result = InvitePrefill.Validate(prefill);

        // The key is fine and the value is not, so reporting this as an unknown key would tell the
        // operator that 'name' is not a field the booking form accepts. It is.
        result.Problem.ShouldBe(PrefillProblem.NullValue);
        result.OffendingKey.ShouldBe(Name);
    }

    /// <summary>
    /// Bounded against the booking payload's own limit rather than a copy of it. Unbounded, a
    /// prefill longer than the field it seeds produces an invite that is fine until somebody opens
    /// it — and then refuses their booking over something they did not write.
    /// </summary>
    [Fact]
    public void Validate_AValueLongerThanTheBookingPayloadAccepts_IsRefused()
    {
        var prefill = new Dictionary<string, string>
        {
            [Notes] = new(Filler, CreateBookingRequest.MaxNotesLength + 1),
        };

        var result = InvitePrefill.Validate(prefill);

        result.Problem.ShouldBe(PrefillProblem.TooLong);
        result.OffendingKey.ShouldBe(Notes);
    }

    /// <summary>The bound itself is a length the payload accepts, so the invite that seeds it is accepted too.</summary>
    [Fact]
    public void Validate_AValueExactlyAtTheBookingPayloadsLimit_IsAccepted()
    {
        var prefill = new Dictionary<string, string>
        {
            [Notes] = new(Filler, CreateBookingRequest.MaxNotesLength),
        };

        InvitePrefill.Validate(prefill).IsValid.ShouldBeTrue();
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
