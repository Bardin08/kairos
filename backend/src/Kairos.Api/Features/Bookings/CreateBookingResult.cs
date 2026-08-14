using System.Text;

namespace Kairos.Api.Features.Bookings;

/// <summary>
/// The outcome of one booking attempt. On success <see cref="ManageToken"/> is the only time the
/// plaintext token exists — the database keeps its hash — and <see cref="Reason"/> is null. On
/// failure the reason is a stable machine code, not a sentence: the endpoint chooses the wording.
/// </summary>
public readonly record struct CreateBookingResult(bool Success, Guid Id, string? ManageToken, string? Reason)
{
    /// <summary>
    /// The manage token is a bearer capability: whoever holds it can cancel or move the meeting.
    /// The synthesized ToString would print it in full, so one logger.LogInformation("{Result}")
    /// would write it to disk. The identifier is enough to find the row from a log line.
    /// </summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"{nameof(Success)} = {Success}, ");
        builder.Append($"{nameof(Id)} = {Id}, ");
        builder.Append($"{nameof(ManageToken)} = {(ManageToken is null ? "null" : "[redacted]")}, ");
        builder.Append($"{nameof(Reason)} = {Reason ?? "null"}");
        return true;
    }
}
