namespace Kairos.Api.Integrations;

/// <summary>
/// What a provider hands back once the meeting is in its calendar.
/// </summary>
/// <param name="ExternalEventId">The handle Kairos moves and withdraws the event by.</param>
/// <param name="CalendarUid">
/// The RFC 5545 UID the provider minted, which the confirmation's .ics quotes so that the
/// invitation and the event are one meeting rather than two. Null when a provider exposes none;
/// the mail then falls back to the booking id, which is what shipped before this existed. Never
/// assembled here — a UID built from a pattern is a vendor detail leaking into neutral code.
/// </param>
public sealed record PlacedEvent(string ExternalEventId, string? CalendarUid);
