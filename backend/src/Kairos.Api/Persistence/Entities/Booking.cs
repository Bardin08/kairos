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

    /// <summary>
    /// Advanced by every notice that revises the meeting. The .ics SEQUENCE reads this.
    /// </summary>
    public int Sequence { get; set; }

    public long CreatedAt { get; set; }

    public long? CancelledAt { get; set; }

    /// <summary>
    /// Why the operator called it off, in their words, and null for every booking the invitee
    /// cancelled themselves. The null is the record of which of the two happened, which is why
    /// there is no second column saying who.
    /// </summary>
    public string? CancellationReason { get; set; }

    public ICollection<BookingParticipant> Participants { get; set; } = [];

    // Reserved for phase 4 recurrence. Unused here; free to add now, expensive later.
    public Guid? SeriesId { get; set; }

    public string? RecurrenceRule { get; set; }

    public long? RecurrenceId { get; set; }
}
