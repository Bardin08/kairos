namespace Kairos.Api.Persistence.Entities;

/// <summary>What happened, from the point of view of whoever subscribed to it.</summary>
public enum OutboxMessageType
{
    BookingCreated = 10,
    BookingCancelled = 20,
    MeetingEnded = 30,
}
