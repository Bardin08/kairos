using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using NodaTime;

namespace Kairos.Api.Integrations.Webhooks;

/// <remarks>
/// It adds and it never saves. That is the whole of the outbox pattern: the message and the state
/// change it describes commit together or neither does. A writer calling SaveChanges on its own
/// behalf would reintroduce the dual-write — a booking with nobody told, or a telling with no
/// booking.
///
/// No "have I enqueued this already?" check either. The unique index on (BookingId, Type) is the
/// guarantee; a pre-check would be check-then-act between two connections, which is a race.
/// </remarks>
public sealed class OutboxWriter(KairosDbContext context, IClock clock)
{
    /// <summary>
    /// Adds the message and returns it, tracked and unsaved. The return value exists
    /// for the one caller that has to take it back: <c>BookingWriter</c> enqueues inside its own
    /// transaction, and a message left in the change tracker after a failure there would ride
    /// along on the next SaveChanges and announce a booking that was never written.
    /// </summary>
    public OutboxMessage Enqueue(Guid bookingId, OutboxMessageType type, string payload)
    {
        var message = new OutboxMessage
        {
            BookingId = bookingId,
            Type = type,
            Payload = payload,
            CreatedAt = clock.GetCurrentInstant().ToUnixTimeSeconds(),
        };

        context.OutboxMessages.Add(message);
        return message;
    }
}
