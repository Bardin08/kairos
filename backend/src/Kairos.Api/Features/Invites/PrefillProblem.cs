namespace Kairos.Api.Features.Invites;

/// <summary>
/// What is wrong with a prefill bag. Its own type rather than a key and a bool, because the three
/// are not the same complaint: reporting a null value as an unknown key would tell the operator
/// that <c>name</c> is not a field the booking form accepts, which is false — the key is fine and
/// the value is not.
/// </summary>
/// <remarks>Numbered from ten with no zero, like every other enum here, so a value nobody set names no problem.</remarks>
public enum PrefillProblem
{
    /// <summary>A key the booking form has no input for. Storing it would seed nothing.</summary>
    UnknownKey = 10,

    /// <summary>
    /// A fillable key carrying JSON null. It binds — System.Text.Json ignores the dictionary's
    /// nullable-reference annotation at runtime — and is read back typed non-null, so it reaches
    /// the invitee's form as a null in a controlled input and their own booking POST then fails
    /// <c>[Required]</c> with a 400 they cannot fix.
    /// </summary>
    NullValue = 20,

    /// <summary>
    /// A value longer than the booking payload itself accepts. Unbounded, a 5,000-character notes
    /// prefill produces an invite that is fine until somebody opens it.
    /// </summary>
    TooLong = 30,
}
