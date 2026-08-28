using System.Text;

using Kairos.Api.Persistence.Entities;

namespace Kairos.Api.Features.Invites;

/// <summary>
/// What <see cref="InviteWriter.Issue"/> decided.
/// </summary>
/// <param name="Prefill">
/// The verdict on the bag, for <see cref="IssueRefusal.PrefillInvalid"/> alone, and null on every
/// other outcome. The whole verdict rather than the key off it: the operator reads a sentence
/// naming the key AND what was wrong with it, and "one of these is wrong" is not a fixable
/// complaint.
/// <para>
/// That pairing — this present exactly when the refusal is <see cref="IssueRefusal.PrefillInvalid"/>
/// — is what the endpoint reads the message off. There is deliberately no general
/// <c>Refused(refusal, prefill)</c>: the two factories below each take exactly the fields their
/// own refusal has, so a mismatched pair is not something a caller can ask for. As one factory
/// taking both it was a comment away from a 500 where a 400 was meant.
/// </para>
/// </param>
public readonly record struct IssueResult(
    PersonalInvite? Invite,
    string? Token,
    IssueRefusal? Refusal,
    PrefillValidation? Prefill)
{
    public static IssueResult Issued(PersonalInvite invite, string token) =>
        new(invite, token, null, null);

    /// <summary>Takes the verdict rather than a refusal, so there is no way to name this refusal without one.</summary>
    public static IssueResult RefusedPrefill(PrefillValidation prefill) =>
        new(null, null, IssueRefusal.PrefillInvalid, prefill);

    /// <summary>Takes nothing: a deadline in the past is the whole complaint, and names no key.</summary>
    public static IssueResult RefusedDeadline() =>
        new(null, null, IssueRefusal.DeadlinePast, null);

    /// <summary>
    /// The token is a bearer capability: whoever holds it can book as the invitee. The synthesized
    /// ToString would print it in full, so one logger.LogInformation("{Result}") would write it to
    /// disk. Redacted the way <see cref="Bookings.CreateBookingResult"/> redacts its manage token,
    /// and the invite prints as its identifier for the same reason that one does: an id is enough
    /// to find the row from a log line, and it cannot start leaking a column if the entity ever
    /// grows a ToString of its own.
    /// </summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"{nameof(Invite)} = {Invite?.Id.ToString() ?? "null"}, ");
        builder.Append($"{nameof(Token)} = {(Token is null ? "null" : "[redacted]")}, ");
        builder.Append($"{nameof(Refusal)} = {Refusal?.ToString() ?? "null"}, ");
        builder.Append($"{nameof(Prefill)} = {Prefill?.ToString() ?? "null"}");
        return true;
    }
}
