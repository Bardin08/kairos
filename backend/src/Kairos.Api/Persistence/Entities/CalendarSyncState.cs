namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// How far a booking has got towards existing in the operator's calendar. Failed is a resting
/// state, not an error path: a provider failure never fails a booking, so the row records what
/// went wrong and the booking stands.
/// </summary>
public enum CalendarSyncState
{
    Pending = 10,
    Synced = 20,
    Failed = 30,

    /// <summary>
    /// The event was withdrawn from the provider's calendar because the booking was cancelled.
    /// A resting state like Failed, and a row rather than a deletion: the id of the event that
    /// was withdrawn is the only evidence this booking was ever in a calendar, and losing it
    /// makes a meeting somebody cancelled indistinguishable from one that never synced.
    /// </summary>
    Cancelled = 40,
}
