using Kairos.Api.Persistence.Entities;

namespace Kairos.Api.Integrations;

/// <summary>
/// Putting a meeting into the organizer's calendar, moving it, and taking it back out. One
/// implementation per provider; <see cref="CalendarSyncService"/> is written against this and
/// never against a vendor's client, which is the whole reason the interface exists rather than
/// the adapter being injected directly.
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
    ///
    /// False means nobody ever connected one. A grant that was connected and has since been
    /// withdrawn throws from here instead, because the credential is refreshed to answer the
    /// question at all — and it has to stay a throw: answering false would record no failure and
    /// report a revoked account as a calendar that was never set up.
    /// </summary>
    public Task<bool> IsConnectedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates the event and returns the provider's id for it. Throws on anything that means the
    /// meeting is not in the calendar; the caller records that rather than propagating it.
    /// </summary>
    public Task<string> CreateEventAsync(CalendarEventRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Moves the event <paramref name="externalEventId"/> names to the times and text in
    /// <paramref name="request"/>, leaving everything the request does not describe exactly as it
    /// was. Throws on anything that means the event is still at its old time.
    /// </summary>
    /// <remarks>
    /// A move, never a replacement, and that is why nothing comes back. The id stays the id
    /// <see cref="CreateEventAsync"/> returned: every attendee who has already accepted holds a
    /// calendar entry keyed on it, so a delete-and-recreate would read to all of them as this
    /// meeting being cancelled and a different one appearing, and it would orphan the conference
    /// Kairos recorded against the old event. A method that handed back a new id would be an
    /// invitation to implement it that way; there is no new identity to report, so there is no
    /// return value.
    ///
    /// The request carries no diff and no notion of what changed, for the same reason
    /// <see cref="CalendarEventRequest"/> carries no calendar id: a booking knows what it is now,
    /// not what it used to be, and working out which fields moved is the provider's problem.
    /// </remarks>
    public Task UpdateEventAsync(
        string externalEventId,
        CalendarEventRequest request,
        CancellationToken cancellationToken);

    /// <summary>Removes an event previously created here, by the id <see cref="CreateEventAsync"/> returned.</summary>
    public Task CancelEventAsync(string externalEventId, CancellationToken cancellationToken);
}
