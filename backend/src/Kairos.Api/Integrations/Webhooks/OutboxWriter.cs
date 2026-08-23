using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using NodaTime;

namespace Kairos.Api.Integrations.Webhooks;

/// <remarks>
/// It adds and it never saves. That is the whole of the outbox pattern: the message and the
/// state change it describes commit together or neither does. A writer that called SaveChanges
/// on its own behalf would put them in two transactions and reintroduce exactly the dual-write
/// the pattern exists to remove — a booking with nobody told, or a telling with no booking.
///
/// There is also no "have I enqueued this already?" check, on purpose. The unique index on
/// (BookingId, Type) is the guarantee; a duplicate insert fails and the caller swallows it. A
/// pre-check would be check-then-act between two connections, which is to say a race.
/// </remarks>
public sealed class OutboxWriter(KairosDbContext context, IClock clock)
{
    /// <summary>
    /// Adds the message and returns it, untracked-by-nobody and unsaved. The return value exists
    /// for the one caller that has to be able to take it back: <c>BookingWriter</c> enqueues
    /// inside its own transaction, and a failure there has to detach exactly what it added — a
    /// message left in the change tracker would ride along on the caller's next SaveChanges and
    /// announce a booking that was never written.
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
