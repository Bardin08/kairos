using Kairos.Api.Persistence.Entities;

namespace Kairos.Api.Integrations;

/// <summary>
/// Putting a meeting into the organizer's calendar, and taking it back out. One implementation
/// per provider; <see cref="CalendarSyncService"/> is written against this and never against a
/// vendor's client, which is the whole reason the interface exists rather than the adapter being
/// injected directly.
/// </summary>
/// <remarks>
/// Nothing here persists anything. An implementation talks to its provider and returns, and
/// <see cref="CalendarSyncService"/> owns every row that results — so a second provider inherits
/// the state machine, the failure recording and the transaction boundary for free instead of
/// re-implementing them slightly differently.
/// </remarks>
public interface ICalendarWriter
{
    public IntegrationProvider Provider { get; }

    /// <summary>
    /// Whether there is an account to write to at all. Unconfigured and never-connected are the
    /// ordinary state of a fresh self-hosted instance, not faults, and the caller has to be able
    /// to tell them from a provider that was asked and refused — one is nothing to report and the
    /// other is a sync failure an operator needs to see. Without this the two are the same
    /// exception and every booking on a deployment that never intended to sync carries a red mark.
    /// </summary>
    public Task<bool> IsConnectedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates the event and returns the provider's id for it. Throws on anything that means the
    /// meeting is not in the calendar; the caller records that rather than propagating it.
    /// </summary>
    public Task<string> CreateEventAsync(CalendarEventRequest request, CancellationToken cancellationToken);

    /// <summary>Removes an event previously created here, by the id <see cref="CreateEventAsync"/> returned.</summary>
    public Task CancelEventAsync(string externalEventId, CancellationToken cancellationToken);
}
