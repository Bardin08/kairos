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
}
