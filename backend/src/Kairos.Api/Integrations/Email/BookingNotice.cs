namespace Kairos.Api.Integrations.Email;

/// <summary>
/// One per thing that happens to a meeting the invitee has to be told about, each needing its own
/// sentence and its own iCalendar METHOD — or, for a proposal, no calendar part at all.
/// </summary>
/// <remarks>
/// Passed in rather than inferred from the booking, and that is the whole reason this type
/// exists. The two facts that look like they would give the answer do not: a booking cancelled
/// before it was ever moved and one cancelled after are both <see cref="Persistence.Entities.BookingStatus.Cancelled"/>,
/// and <see cref="Persistence.Entities.Booking.Sequence"/> is above zero for a meeting that moved
/// and for one that moved and was then called off. The caller is the only thing that knows which
/// message it is sending, and a wrong guess here is a <c>CANCEL</c> labelled <c>REQUEST</c> —
/// which clients discard in silence, the same way they discard a stale SEQUENCE.
/// </remarks>
public enum BookingNotice
{
    Confirmation = 10,
    Reschedule = 20,
    Cancellation = 30,

    /// <summary>
    /// A new time is being offered and nothing has changed yet. The only notice with no
    /// iCalendar method, because a calendar part here would perform the move it is asking about.
    /// </summary>
    Proposal = 40,
}
