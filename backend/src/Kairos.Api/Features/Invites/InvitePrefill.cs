using System.Text.Json;

namespace Kairos.Api.Features.Invites;

/// <summary>
/// The prefill bag: what an invite carries into the booking form, keyed by the booking payload's
/// own serialized field names — <c>notes</c>, not <c>Notes</c> — so that a prefill is a partial
/// <c>CreateBookingRequest</c>.
/// </summary>
/// <remarks>
/// The accepted keys are listed here rather than reflected off that record, and that is
/// deliberate. Only three of its seven fields are the operator's to fill: <c>slug</c> is the link,
/// <c>start</c> and <c>durationMinutes</c> are the invitee's to choose, and <c>timeZone</c> is the
/// one the invitee reads the times in. Reflection would accept those four as if they meant
/// something, and would silently accept whatever field lands on that record next. When custom
/// booking questions arrive, this list is what they extend.
/// </remarks>
public static class InvitePrefill
{
    /// <summary>
    /// The stored form of a bag with nothing in it, which is a valid invite. Named once so that
    /// nothing seeds a row, or asserts on one, by retyping the literal.
    /// </summary>
    public const string Empty = "{}";

    /// <summary>What the operator may fill in on the invitee's behalf, today.</summary>
    private static readonly HashSet<string> Fillable =
        new(StringComparer.Ordinal) { "name", "email", "notes" };

    public static PrefillValidation Validate(IReadOnlyDictionary<string, string> prefill)
    {
        foreach (var key in prefill.Keys)
        {
            if (!Fillable.Contains(key))
            {
                return new PrefillValidation(false, key);
            }
        }

        return PrefillValidation.Valid;
    }

    public static string Serialize(IReadOnlyDictionary<string, string> prefill) =>
        JsonSerializer.Serialize(prefill);

    /// <summary>
    /// Reading is deliberately more permissive than writing. A stored key this version does not
    /// know is carried through rather than rejected: the invite is still spendable, and the page
    /// simply has no input to put it in.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Read(string stored) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(stored) ?? [];
}
