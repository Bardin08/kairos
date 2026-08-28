using System.Text.Json;

using Kairos.Api.Features.Bookings;

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

    /// <summary>
    /// What the operator may fill in on the invitee's behalf, today, each against the bound
    /// <see cref="CreateBookingRequest"/> gives the field it seeds. The bounds are that record's
    /// own constants rather than copies: a prefill the booking payload would refuse is an invite
    /// that is fine until somebody opens it.
    /// </summary>
    private static readonly Dictionary<string, int> Fillable = new(StringComparer.Ordinal)
    {
        ["name"] = CreateBookingRequest.MaxNameLength,
        ["email"] = CreateBookingRequest.MaxEmailLength,
        ["notes"] = CreateBookingRequest.MaxNotesLength,
    };

    /// <summary>
    /// The keys AND the values. Inspecting the keys alone let two bags through that the booking
    /// form cannot use, and neither announces itself until an invitee opens the URL.
    /// </summary>
    /// <remarks>
    /// No format check on the email, deliberately: the invitee can edit every field, the booking
    /// payload validates the address they actually submit, and an operator's typo is not something
    /// to refuse a whole invite over.
    /// </remarks>
    public static PrefillValidation Validate(IReadOnlyDictionary<string, string> prefill)
    {
        foreach (var (key, value) in prefill)
        {
            if (!Fillable.TryGetValue(key, out var maxLength))
            {
                return new PrefillValidation(PrefillProblem.UnknownKey, key);
            }

            if (value is null)
            {
                return new PrefillValidation(PrefillProblem.NullValue, key);
            }

            if (value.Length > maxLength)
            {
                return new PrefillValidation(PrefillProblem.TooLong, key);
            }
        }

        return PrefillValidation.Valid;
    }

    /// <summary>
    /// Why a bag was refused, in a sentence naming the key and what was wrong with it. Here rather
    /// than at the endpoint because the bound it quotes is this file's to know, and because the
    /// one code every prefill problem answers with is only fixable if the message says which.
    /// </summary>
    public static string Describe(PrefillValidation validation) => validation.Problem switch
    {
        PrefillProblem.UnknownKey =>
            $"'{validation.OffendingKey}' is not a field the booking form accepts.",
        PrefillProblem.NullValue =>
            $"'{validation.OffendingKey}' was sent with no value. Leave the key out instead.",
        PrefillProblem.TooLong =>
            $"'{validation.OffendingKey}' is longer than the "
            + $"{Fillable[validation.OffendingKey!]} characters the booking form accepts.",
        _ => throw new ArgumentOutOfRangeException(
            nameof(validation), validation, "That bag was not refused."),
    };

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
