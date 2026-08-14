namespace Kairos.Api.Persistence.Entities;

public sealed class Booking
{
    /// <summary>UUIDv7. The public identifier: manage URL, calendar event, webhook payloads.</summary>
    public Guid Id { get; set; }

    public Guid LinkId { get; set; }

    public Link Link { get; set; } = null!;

    /// <summary>Unix seconds. Margin arithmetic happens in SQL, so integers rather than text.</summary>
    public long StartUtc { get; set; }

    public long EndUtc { get; set; }

    public string Notes { get; set; } = string.Empty;

    public BookingStatus Status { get; set; }

    public required string ManageTokenHash { get; set; }

    /// <summary>Incremented on reschedule. Part B's .ics SEQUENCE reads this.</summary>
    public int Sequence { get; set; }

    public long CreatedAt { get; set; }

    public long? CancelledAt { get; set; }

    public ICollection<BookingParticipant> Participants { get; set; } = [];

    // Reserved for phase 4 recurrence. Unused here; free to add now, expensive later.
    public Guid? SeriesId { get; set; }

    public string? RecurrenceRule { get; set; }

    public long? RecurrenceId { get; set; }
}
