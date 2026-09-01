namespace Kairos.Api.Integrations.Email;

/// <summary>
/// One per thing that happens to a meeting the invitee has to be told about, each needing its own
/// sentence and its own iCalendar METHOD — or, for a proposal or a reminder, no calendar part at
/// all.
/// </summary>
/// <remarks>
/// Passed in rather than inferred from the booking, which is the whole reason this type exists.
/// Neither fact that looks like it would answer does: a booking cancelled before it was ever moved
/// and one cancelled after are both <see cref="Persistence.Entities.BookingStatus.Cancelled"/>,
/// and <see cref="Persistence.Entities.Booking.Sequence"/> is above zero for both. Only the caller
/// knows which message it is sending, and a wrong guess is a <c>CANCEL</c> labelled
/// <c>REQUEST</c>, which clients discard in silence.
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

    /// <summary>
    /// A day before, nothing has changed and nothing needs answering. The only invitee notice
    /// besides <see cref="Proposal"/> with no calendar part, and for the opposite reason: a
    /// proposal must not perform the move it asks about, and this has nothing to perform.
    /// </summary>
    Reminder = 50,
}
