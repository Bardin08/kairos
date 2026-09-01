using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;

using Shouldly;

namespace Kairos.Api.Tests.Persistence;

/// <summary>What the table guarantees on its own rather than by every writer remembering to.</summary>
public class ReminderSchemaTests
{
    private const long SomeDueAt = 1_800_000_000;

    private const string ReminderPatternName = "Reminder pattern";
    private const string ReminderSlug = "reminder";
    private const string ReminderLinkName = "Reminder link";
    private const string ReminderManageHash = "reminder-manage-hash";
    private const long BookingStartUtc = 1_900_000_000;
    private const int BookingMinutes = 30;
    private const int SecondsPerMinute = 60;

    /// <summary>
    /// Cancelling a booking deletes its reminder, and the database agrees rather than leaving a
    /// row pointing at a meeting that is gone: an intention to remind somebody about a meeting
    /// that no longer exists is not one.
    /// </summary>
    /// <remarks>
    /// The write and the delete go through separate contexts, on purpose: a <see cref="Reminder"/>
    /// tracked by the same context that removes its <see cref="Booking"/> gets deleted by EF's
    /// own client-side cascade, driven by the fluent config, before SQLite's <c>ON DELETE
    /// CASCADE</c> ever has to act. Reloading the booking in a fresh context leaves the reminder
    /// untracked, so the only thing left to delete it is the schema this test is meant to prove.
    /// </remarks>
    [Fact]
    public async Task DeletingABooking_TakesItsReminderWithIt()
    {
        using var database = new TestDatabase();

        Guid bookingId;
        await using (var writer = database.CreateContext())
        {
            var booking = SeedBooking(writer);
            bookingId = booking.Id;
            writer.Reminders.Add(new Reminder { BookingId = booking.Id, DueAt = SomeDueAt });
            await writer.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var deleter = database.CreateContext())
        {
            var booking = await deleter.Bookings.SingleAsync(
                row => row.Id == bookingId, TestContext.Current.CancellationToken);
            deleter.Bookings.Remove(booking);
            await deleter.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var reader = database.CreateContext();
        (await reader.Reminders.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    private static Booking SeedBooking(KairosDbContext context)
    {
        var pattern = new Pattern { Id = Guid.CreateVersion7(), Name = ReminderPatternName };
        var link = new Link
        {
            Id = Guid.CreateVersion7(),
            PatternId = pattern.Id,
            Slug = ReminderSlug,
            Name = ReminderLinkName,
            Active = true,
        };
        var booking = new Booking
        {
            Id = Guid.CreateVersion7(),
            LinkId = link.Id,
            StartUtc = BookingStartUtc,
            EndUtc = BookingStartUtc + (BookingMinutes * SecondsPerMinute),
            Status = BookingStatus.Confirmed,
            ManageTokenHash = ReminderManageHash,
            CreatedAt = BookingStartUtc,
        };

        context.Patterns.Add(pattern);
        context.Links.Add(link);
        context.Bookings.Add(booking);
        context.SaveChanges();

        return booking;
    }
}
