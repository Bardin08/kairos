using Kairos.Api.Features.Bookings;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The one place double-booking can happen, so the one place a sequential test would lie. The
/// first case runs two real threads against one file-backed WAL database; the other two pin the
/// conflict rule the SQL encodes — shared across links, half-open, max of the two margins.
/// </summary>
public class BookingConcurrencyTests
{
    private const int NoMargin = 0;
    private const int ExistingMeetingMarginMinutes = 30;
    private const int SlotMinutes = 30;
    private const int RacingCallers = 2;
    private const string ConsultingSlug = "consulting";
    private const string MentorshipSlug = "mentorship";

    private static readonly Guid ConsultingId = Guid.CreateVersion7();
    private static readonly Guid MentorshipId = Guid.CreateVersion7();
    private static readonly Instant MeetingStart = Instant.FromUtc(2026, 9, 14, 10, 0);
    private static readonly Duration SlotDuration = Duration.FromMinutes(SlotMinutes);
    private static readonly BookerDetails Booker = new("Ada", "ada@example.com", "Europe/Warsaw");

    [Fact]
    public void TryCreate_TwoConcurrentRequestsForOneSlot_CreatesExactlyOne()
    {
        using var database = new TestDatabase();
        using (var seed = database.CreateContext())
        {
            Seed(seed, NoMargin);
        }

        var results = new CreateBookingResult[RacingCallers];

        // Every context is built — and every migration run — before anyone books, so the two
        // threads meet at the barrier with nothing left to do but contend for the write lock.
        using var barrier = new Barrier(RacingCallers);
        var callers = new Thread[RacingCallers];
        for (var index = 0; index < callers.Length; index++)
        {
            var slot = index;
            callers[slot] = new Thread(() =>
            {
                using var context = database.CreateContext();
                var writer = new BookingWriter(context, SystemClock.Instance);
                barrier.SignalAndWait();
                results[slot] = writer.TryCreate(
                    ConsultingId, MeetingStart, SlotDuration, Booker, string.Empty, NoMargin);
            });
            callers[slot].Start();
        }

        foreach (var caller in callers)
        {
            caller.Join();
        }

        results.Count(result => result.Success).ShouldBe(1);
        results.Single(result => !result.Success).Reason.ShouldBe(BookingWriter.SlotTakenReason);
        results.Single(result => result.Success).ManageToken.ShouldNotBeNullOrWhiteSpace();

        using var read = database.CreateContext();
        read.Bookings.Count(booking => booking.Status == BookingStatus.Confirmed).ShouldBe(1);
    }

    [Fact]
    public void TryCreate_OverlappingSlotOnAnotherLink_IsRejected()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        Seed(context, NoMargin);
        var writer = new BookingWriter(context, SystemClock.Instance);

        writer.TryCreate(ConsultingId, MeetingStart, SlotDuration, Booker, string.Empty, NoMargin)
            .Success.ShouldBeTrue();

        // Halfway into the consulting meeting, on the other link. Availability is one calendar,
        // not one calendar per link, so the second link may not sell the same half hour.
        var overlapping = MeetingStart + Duration.FromMinutes(SlotMinutes / 2);
        var result = writer.TryCreate(
            MentorshipId, overlapping, SlotDuration, Booker, string.Empty, NoMargin);

        result.Success.ShouldBeFalse();
        result.Reason.ShouldBe(BookingWriter.SlotTakenReason);
        context.Bookings.Count(booking => booking.Status == BookingStatus.Confirmed).ShouldBe(1);
    }

    [Fact]
    public void TryCreate_ExistingMeetingHasTheLargerMargin_BlocksTheZeroMarginLink()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        Seed(context, ExistingMeetingMarginMinutes);
        var writer = new BookingWriter(context, SystemClock.Instance);

        // 10:00-11:00 on the link that wants thirty minutes either side of itself.
        var existing = Duration.FromMinutes(SlotMinutes * 2);
        writer.TryCreate(
                ConsultingId, MeetingStart, existing, Booker, string.Empty, ExistingMeetingMarginMinutes)
            .Success.ShouldBeTrue();

        // The zero-margin link inherits the larger of the two margins, not the sum: the meeting
        // is blocked until 11:30, so 11:15 is refused and 11:30 — half-open — is offered.
        var tooSoon = MeetingStart + existing + Duration.FromMinutes(SlotMinutes / 2);
        var justInTime = MeetingStart + existing + Duration.FromMinutes(ExistingMeetingMarginMinutes);

        writer.TryCreate(MentorshipId, tooSoon, SlotDuration, Booker, string.Empty, NoMargin)
            .Reason.ShouldBe(BookingWriter.SlotTakenReason);
        writer.TryCreate(MentorshipId, justInTime, SlotDuration, Booker, string.Empty, NoMargin)
            .Success.ShouldBeTrue();
    }

    private static void Seed(KairosDbContext context, int consultingMarginMinutes)
    {
        var pattern = new Pattern { Id = Guid.CreateVersion7(), Name = "Weekdays" };
        context.Patterns.Add(pattern);
        context.Links.AddRange(
            new Link
            {
                Id = ConsultingId,
                Slug = ConsultingSlug,
                Name = "Consulting",
                PatternId = pattern.Id,
                MarginMinutes = consultingMarginMinutes,
            },
            new Link
            {
                Id = MentorshipId,
                Slug = MentorshipSlug,
                Name = "Mentorship",
                PatternId = pattern.Id,
                MarginMinutes = NoMargin,
            });
        context.SaveChanges();
    }
}
