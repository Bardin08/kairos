using Kairos.Api.Availability;
using Kairos.Api.Features.Bookings;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Scheduling;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Availability;

/// <summary>
/// What lands in the shared conflict pool, and with whose margin. A block whose margin is read
/// off the wrong row pads by the wrong amount three layers down, where nothing names this class.
/// </summary>
public class LocalBusySourceTests
{
    private const int ConsultingMarginMinutes = 45;
    private const int MeetingMinutes = 30;

    private static readonly Guid ConsultingId = Guid.CreateVersion7();
    private static readonly Guid MentorshipId = Guid.CreateVersion7();
    private static readonly Duration Meeting = Duration.FromMinutes(MeetingMinutes);

    [Fact]
    public async Task GetBusyAsync_OneLinkOverridesTheMargin_ProjectsTheOverrideAndInheritsTheGlobalOtherwise()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        Seed(context);
        AddBooking(context, ConsultingId, Instant.FromUtc(2026, 9, 14, 10, 0), BookingStatus.Confirmed);
        AddBooking(context, MentorshipId, Instant.FromUtc(2026, 9, 14, 12, 0), BookingStatus.Confirmed);
        AddBooking(context, ConsultingId, Instant.FromUtc(2026, 9, 14, 14, 0), BookingStatus.Cancelled);

        var busy = await new LocalBusySource(context, SystemClock.Instance).GetBusyAsync(
            Window(Instant.FromUtc(2026, 9, 14, 9, 0), Instant.FromUtc(2026, 9, 14, 16, 0)),
            TestContext.Current.CancellationToken);

        // The cancelled one is gone; the two live ones carry the margin of the link they belong
        // to, not of whoever is asking.
        busy.Select(block => block.MarginMinutes)
            .ShouldBe([ConsultingMarginMinutes, SchedulingDefaults.MarginMinutes], ignoreOrder: true);
    }

    [Fact]
    public async Task GetBusyAsync_BookingEndsBeforeTheWindowButItsOwnMarginReachesIn_IsStillReturned()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        Seed(context);

        // Consulting runs 08:30-09:00 and demands 45 minutes after itself, so it reaches 09:45
        // and into the window; mentorship runs 08:00-08:30, inherits the global 15, stops at 08:45.
        AddBooking(context, ConsultingId, Instant.FromUtc(2026, 9, 14, 8, 30), BookingStatus.Confirmed);
        AddBooking(context, MentorshipId, Instant.FromUtc(2026, 9, 14, 8, 0), BookingStatus.Confirmed);

        var busy = await new LocalBusySource(context, SystemClock.Instance).GetBusyAsync(
            Window(Instant.FromUtc(2026, 9, 14, 9, 30), Instant.FromUtc(2026, 9, 14, 12, 0)),
            TestContext.Current.CancellationToken);

        busy.Select(block => block.Interval.Start)
            .ShouldBe([Instant.FromUtc(2026, 9, 14, 8, 30)]);
    }

    /// <remarks>Qualified: NodaTime has an Interval too, and both namespaces are in scope here.</remarks>
    private static Kairos.Api.Scheduling.Interval Window(Instant start, Instant end) => new(start, end);

    private static void AddBooking(KairosDbContext context, Guid linkId, Instant start, BookingStatus status)
    {
        context.Bookings.Add(new Booking
        {
            Id = Guid.CreateVersion7(),
            LinkId = linkId,
            StartUtc = start.ToUnixTimeSeconds(),
            EndUtc = (start + Meeting).ToUnixTimeSeconds(),
            Status = status,
            ManageTokenHash = BookingWriter.Hash(Guid.CreateVersion7().ToString()),
            CreatedAt = start.ToUnixTimeSeconds(),
        });
        context.SaveChanges();
    }

    private static void Seed(KairosDbContext context)
    {
        var pattern = new Pattern { Id = Guid.CreateVersion7(), Name = "Weekdays" };
        context.Patterns.Add(pattern);
        context.Links.AddRange(
            new Link
            {
                Id = ConsultingId,
                Slug = "consulting",
                Name = "Consulting",
                PatternId = pattern.Id,
                MarginMinutes = ConsultingMarginMinutes,
            },
            new Link
            {
                Id = MentorshipId,
                Slug = "mentorship",
                Name = "Mentorship",
                PatternId = pattern.Id,
                MarginMinutes = null,
            });
        context.SaveChanges();
    }
}
