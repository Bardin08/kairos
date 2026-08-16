namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// Something that happened, waiting to be told to whoever subscribed. Written in the same
/// transaction as the change it describes, which is what makes "the booking exists but nobody
/// was told" impossible rather than unlikely.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }

    public Guid BookingId { get; set; }

    public Booking Booking { get; set; } = null!;

    public OutboxMessageType Type { get; set; }

    /// <summary>The JSON body as it will be delivered. Rendered once, at enqueue time.</summary>
    public required string Payload { get; set; }

    public long CreatedAt { get; set; }

    /// <summary>Null while undelivered. The dispatcher's queue is the index on this column.</summary>
    public long? DispatchedAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}
