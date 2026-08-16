namespace Kairos.Api.Integrations;

/// <summary>
/// How a meeting is joined, in the two facts every conferencing provider has: a handle it calls
/// the conference by, and a URL a human clicks.
/// </summary>
/// <remarks>
/// <see cref="JoinUrl"/> is a <see cref="Uri"/> rather than a string on purpose. It is rendered
/// into an email, an <c>.ics</c> and a page, and a provider that hands back something that is not
/// a URL is a failure worth catching at the seam that received it rather than one that reaches an
/// invitee as an unclickable line of text.
/// </remarks>
/// <param name="ExternalId">The provider's own id for the conference. Not the URL, and not the
/// calendar event's id — a conference outlives whichever event happened to carry it.</param>
public sealed record ConferenceDetails(string ExternalId, Uri JoinUrl);
