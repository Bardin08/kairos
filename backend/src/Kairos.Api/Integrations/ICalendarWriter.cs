using Kairos.Api.Persistence.Entities;

namespace Kairos.Api.Integrations;

/// <summary>
/// One implementation per provider. <see cref="CalendarSyncService"/> is written against this and
/// never against a vendor's client, which is the whole reason the interface exists rather than
/// the adapter being injected directly.
/// </summary>
/// <remarks>
/// Nothing here persists anything: an implementation talks to its provider and returns, and
/// <see cref="CalendarSyncService"/> owns every row, so a second provider inherits the state
/// machine, the failure recording and the transaction boundary.
/// </remarks>
public interface ICalendarWriter
{
    public IntegrationProvider Provider { get; }

    /// <summary>
    /// False means nobody ever connected an account — the ordinary state of a fresh self-hosted
    /// instance. Without it, that is indistinguishable from a provider asked and refused, and
    /// every booking on a deployment that never meant to sync carries a red mark.
    ///
    /// A grant since withdrawn throws instead: the credential is refreshed to answer at all, and
    /// answering false would record no failure and report a revoked account as one never set up.
    /// </summary>
    public Task<bool> IsConnectedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Throws on anything that means the meeting is not in the calendar; the caller records that
    /// rather than propagating it.
    /// </summary>
    public Task<PlacedEvent> CreateEventAsync(
        CalendarEventRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Leaves everything <paramref name="request"/> does not describe exactly as it was. Throws on
    /// anything that means the event is still at its old time.
    /// </summary>
    /// <remarks>
    /// A move, never a replacement, which is why nothing comes back. The id stays the one
    /// <see cref="CreateEventAsync"/> returned: every attendee who accepted holds a calendar entry
    /// keyed on it, so delete-and-recreate would read as a cancellation plus a new meeting and
    /// would orphan the conference recorded against the old event.
    ///
    /// The request carries no diff, for the same reason it carries no calendar id: a booking knows
    /// what it is now, not what it used to be.
    /// </remarks>
    public Task UpdateEventAsync(
        string externalEventId,
        CalendarEventRequest request,
        CancellationToken cancellationToken);

    public Task CancelEventAsync(string externalEventId, CancellationToken cancellationToken);
}
