using Kairos.Api.Integrations.Webhooks;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Integrations;

/// <summary>
/// The one thing about the outbox worth a test: the unique index is real. Task 9's poller
/// enqueues without asking whether it already has, so if the constraint is not enforced the
/// duplicate goes in silently and every ended meeting is announced twice.
/// </summary>
public class OutboxTests
{
    private const string EmptyPayload = "{}";
    private const long MeetingStartUtc = 1_788_000_000;
    private const long MeetingEndUtc = 1_788_003_600;

    [Fact]
    public void Enqueue_SameBookingAndTypeTwice_IsRejectedByTheUniqueIndex()
    {
        using var database = new TestDatabase();
        var bookingId = SeedBooking(database);

        using var context = database.CreateContext();
        var writer = new OutboxWriter(context, SystemClock.Instance);

        writer.Enqueue(bookingId, OutboxMessageType.MeetingEnded, EmptyPayload);
        context.SaveChanges();

        writer.Enqueue(bookingId, OutboxMessageType.MeetingEnded, EmptyPayload);
        Should.Throw<DbUpdateException>(() => context.SaveChanges());
    }

    private static Guid SeedBooking(TestDatabase database)
    {
        using var context = database.CreateContext();

        var pattern = new Pattern { Id = Guid.CreateVersion7(), Name = "Weekdays" };
        context.Patterns.Add(pattern);

        var link = new Link
        {
            Id = Guid.CreateVersion7(),
            Slug = "consulting",
            Name = "Consulting",
            PatternId = pattern.Id,
        };
        context.Links.Add(link);

        var booking = new Booking
        {
            Id = Guid.CreateVersion7(),
            LinkId = link.Id,
            StartUtc = MeetingStartUtc,
            EndUtc = MeetingEndUtc,
            Status = BookingStatus.Confirmed,
            ManageTokenHash = "hash",
            CreatedAt = MeetingStartUtc,
        };
        context.Bookings.Add(booking);

        context.SaveChanges();
        return booking.Id;
    }
}
