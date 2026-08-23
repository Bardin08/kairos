namespace Kairos.Api.Integrations;

/// <remarks>
/// <see cref="JoinUrl"/> is a <see cref="Uri"/> rather than a string so that a provider handing
/// back something that is not a URL fails at the seam that received it, rather than reaching an
/// invitee as an unclickable line of text in an email, an <c>.ics</c> and a page.
/// </remarks>
/// <param name="ExternalId">Not the calendar event's id — a conference outlives whichever event
/// happened to carry it.</param>
public sealed record ConferenceDetails(string ExternalId, Uri JoinUrl);
