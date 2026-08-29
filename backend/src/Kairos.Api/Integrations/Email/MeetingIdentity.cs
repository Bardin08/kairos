namespace Kairos.Api.Integrations.Email;

/// <summary>
/// Who a meeting belongs to, as an .ics has to say it.
/// </summary>
/// <param name="Uid">
/// RFC 5545 UID. The provider's when one placed the event, so the invitation and the event are
/// one meeting; the booking id otherwise, which is the whole invitation in a deployment with no
/// calendar connected.
/// </param>
/// <param name="OrganizerEmail">The address that owns the meeting.</param>
/// <param name="SentBy">
/// The address that put the message in the post, when it is not the owner. Null when the two are
/// the same address, because a SENT-BY naming the organizer says nothing.
/// </param>
public sealed record MeetingIdentity(string Uid, string OrganizerEmail, string? SentBy);
