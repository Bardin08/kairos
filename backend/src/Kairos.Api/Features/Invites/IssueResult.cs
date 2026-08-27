using System.Text;

using Kairos.Api.Persistence.Entities;

namespace Kairos.Api.Features.Invites;

/// <summary>
/// What <see cref="InviteWriter.Issue"/> decided.
/// </summary>
/// <param name="UnknownKey">
/// The prefill key that sank it, for <see cref="IssueRefusal.PrefillInvalid"/> alone. It is in
/// the message the operator reads, because "one of these is wrong" is not a fixable complaint.
/// </param>
public readonly record struct IssueResult(
    PersonalInvite? Invite,
    string? Token,
    IssueRefusal? Refusal,
    string? UnknownKey)
{
    public static IssueResult Issued(PersonalInvite invite, string token) =>
        new(invite, token, null, null);

    public static IssueResult Refused(IssueRefusal refusal, string? unknownKey) =>
        new(null, null, refusal, unknownKey);

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
        builder.Append($"{nameof(UnknownKey)} = {UnknownKey ?? "null"}");
        return true;
    }
}
