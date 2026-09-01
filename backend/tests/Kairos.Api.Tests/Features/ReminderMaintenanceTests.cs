using Kairos.Api.Availability;
using Kairos.Api.Features.Bookings;
using Kairos.Api.Integrations.Webhooks;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Scheduling;
using Kairos.Api.Security;

using Microsoft.EntityFrameworkCore;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// What happens to a scheduled reminder when the meeting underneath it changes.
/// </summary>
/// <remarks>
/// The two paths are already single — <see cref="BookingMover.MoveToAsync"/> is the only place
/// StartUtc changes, and <see cref="BookingCanceller"/> the only place a booking stays cancelled —
/// which is what makes a materialized due moment affordable. These tests are what hold that in
/// place.
///
/// The booking under each case is written by <see cref="BookingWriter"/> rather than seeded as
/// rows, so the reminder being maintained here is the one the application actually writes.
/// </remarks>
public class ReminderMaintenanceTests
{
    private const int NoMargin = 0;
    private const int MeetingMinutes = 30;

    /// <summary>
    /// Far enough out that the seeded booking is always given a reminder, and far enough from
    /// every other instant here that the two due moments cannot be confused.
    /// </summary>
    private const int AWeekAhead = 7;

    private const int ADayLater = 1;

    /// <summary>
    /// Where "moved inside the lead" is placed. Any distance under
    /// <see cref="ReminderSchedule.Lead"/> would do; the boundary itself belongs to
    /// <see cref="ReminderScheduleTests"/>.
    /// </summary>
    private const int InsideTheLeadHours = 2;

    /// <summary>How far back out the third case drags the meeting, past where it began.</summary>
    private const int TwoWeeksAhead = 14;

    private const string Slug = "consulting";
    private const string LinkName = "Consulting call";
    private const string PatternName = "Weekdays";

    private static readonly Guid LinkId = Guid.CreateVersion7();

    /// <summary>Fixed, because every case here is a statement about which side of the lead a start falls on.</summary>
    private static readonly Instant Now = Instant.FromUtc(2026, 9, 7, 9, 0);

    private static readonly Instant OriginalStart = Now + Duration.FromDays(AWeekAhead);
    private static readonly Instant LaterStart = OriginalStart + Duration.FromDays(ADayLater);
    private static readonly Instant InsideTheLeadStart = Now + Duration.FromHours(InsideTheLeadHours);
    private static readonly Instant FinalStart = Now + Duration.FromDays(TwoWeeksAhead);
    private static readonly Duration Meeting = Duration.FromMinutes(MeetingMinutes);
    private static readonly BookerDetails Booker = new("Ada", "ada@example.com", "Europe/Warsaw");

    /// <summary>
    /// Thirty-two zero bytes, base64. A key a test can name, and one no deployment would ever
    /// hold — <see cref="SecretProtector"/> only checks the length.
    /// </summary>
    private static readonly string TestKey = Convert.ToBase64String(new byte[SecretProtector.KeyBytes]);

    [Fact]
    public async Task MoveTo_ALaterTime_MovesTheReminderWithIt()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Book(context, OriginalStart);

        await Mover(context).MoveToAsync(booking, LaterStart, Now);

        var reminder = context.Reminders.AsNoTracking().Single();
        reminder.BookingId.ShouldBe(booking.Id);
        reminder.DueAt.ShouldBe(ReminderSchedule.DueAt(LaterStart).ToUnixTimeSeconds());
    }

    /// <summary>
    /// Moved inside the lead. The reschedule mail the invitee is about to receive IS the notice,
    /// and a reminder firing on the next tick would arrive a minute behind it saying "tomorrow"
    /// about a meeting today.
    /// </summary>
    [Fact]
    public async Task MoveTo_InsideTheLead_DeletesTheReminder()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Book(context, OriginalStart);

        await Mover(context).MoveToAsync(booking, InsideTheLeadStart, Now);

        context.Reminders.AsNoTracking().ShouldBeEmpty();
    }

    /// <summary>
    /// And back out again. A meeting dragged into this evening and then out to next week has a
    /// reminder again — the row was deleted, so this is a write rather than a revival.
    /// </summary>
    [Fact]
    public async Task MoveTo_BackOutsideTheLead_SchedulesOneAgain()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Book(context, OriginalStart);
        var mover = Mover(context);

        await mover.MoveToAsync(booking, InsideTheLeadStart, Now);
        await mover.MoveToAsync(booking, FinalStart, Now);

        context.Reminders.AsNoTracking().Single().DueAt
            .ShouldBe(ReminderSchedule.DueAt(FinalStart).ToUnixTimeSeconds());
    }

    /// <summary>
    /// One confirmed meeting on a seeded link, with whatever reminder the writer decided it gets,
    /// returned tracked because the mover mutates it.
    /// </summary>
    private static Booking Book(KairosDbContext context, Instant start)
    {
        Seed(context);

        var created = Writer(context)
            .TryCreate(LinkId, Slug, start, Meeting, Booker, string.Empty, NoMargin);
        created.Success.ShouldBeTrue();

        return context.Bookings.Single(row => row.Id == created.Id);
    }

    /// <summary>
    /// The mover as the composition root builds it. The slot query is real but never reached:
    /// <see cref="BookingMover.MoveToAsync"/> places a booking, and asking whether the new time is
    /// offered is <see cref="BookingMover.ReleaseAndCheckAsync"/>'s job, one call earlier.
    /// </summary>
    private static BookingMover Mover(KairosDbContext context) =>
        new(context, new SlotQuery(context, new AvailabilityQuery(context), new SlotGenerator(), []));

    /// <summary>
    /// The writer as the composition root builds it, on a clock these tests can place relative to
    /// <see cref="ReminderSchedule.DueAt"/> — the real one cannot promise which side of the lead
    /// the seeded meeting falls on.
    /// </summary>
    private static BookingWriter Writer(KairosDbContext context)
    {
        var clock = new FixedClock(Now);
        return new BookingWriter(
            context,
            clock,
            new OutboxWriter(context, clock),
            new FollowUpWriter(context, new SecretProtector(TestKey), clock));
    }

    private static void Seed(KairosDbContext context)
    {
        var pattern = new Pattern { Id = Guid.CreateVersion7(), Name = PatternName };
        context.Patterns.Add(pattern);
        context.Links.Add(new Link
        {
            Id = LinkId,
            Slug = Slug,
            Name = LinkName,
            PatternId = pattern.Id,
            MarginMinutes = NoMargin,
        });
        context.SaveChanges();
    }
}
