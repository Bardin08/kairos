using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using NodaTime;

namespace Kairos.Api.Integrations.Webhooks;

/// <summary>
/// Records that something happened, in the caller's transaction.
/// </summary>
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
    public void Enqueue(Guid bookingId, OutboxMessageType type, string payload) =>
        context.OutboxMessages.Add(new OutboxMessage
        {
            BookingId = bookingId,
            Type = type,
            Payload = payload,
            CreatedAt = clock.GetCurrentInstant().ToUnixTimeSeconds(),
        });
}
