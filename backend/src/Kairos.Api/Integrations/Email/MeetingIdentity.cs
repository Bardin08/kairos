namespace Kairos.Api.Integrations.Email;

/// <summary>
/// Which meeting a notice is about, and who owns it.
/// </summary>
/// <param name="Uid">
/// RFC 5545 UID. The provider's when one placed the event, so the invitation and the event are
/// one meeting; the booking id otherwise, which is the whole invitation in a deployment with no
/// calendar connected.
/// </param>
/// <param name="Owner">
/// The account whose calendar holds the event, when a provider placed it. Null when none did, and
/// the sending address owns the meeting by default — which is why this is nullable rather than
/// resolved to that address here: the sending address is the mailer's business, and a deployment
/// with no relay configured has none to resolve.
/// </param>
public sealed record MeetingIdentity(string Uid, string? Owner);
