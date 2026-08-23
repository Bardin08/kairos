namespace Kairos.Api.Persistence.Entities;

public enum OutboxMessageType
{
    BookingCreated = 10,
    BookingCancelled = 20,
    MeetingEnded = 30,
}
