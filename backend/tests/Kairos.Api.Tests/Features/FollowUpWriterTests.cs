using Kairos.Api.Features.Bookings;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The row round-trips through the migrated schema: every column set survives a save and a fresh
/// read, including the type conversion on <see cref="FollowUpType"/>.
/// </summary>
public class FollowUpWriterTests
{
    private const long CreatedAtUtc = 1_788_000_000;
    private const long PreviousStartUtc = 1_787_996_400;
    private const int NoAttempts = 0;
    private const string ManageSecret = "protected-manage-token-ciphertext";

    private const string PatternName = "Weekdays";
    private const string Slug = "consulting";
    private const string LinkName = "Consulting call";
    private const string BookerName = "Ada Lovelace";
    private const string BookerEmail = "ada@example.com";
    private const string BookerZone = "Europe/Warsaw";
    private const long MeetingStartUtc = 1_788_000_000;
    private const long MeetingEndUtc = 1_788_003_600;

    [Fact]
    public void FollowUp_Inserted_RoundTripsThroughTheMigratedSchema()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = SeedBooking(context);

        context.FollowUps.Add(new FollowUp
        {
            BookingId = booking.Id,
            Type = FollowUpType.Rescheduled,
            Secret = ManageSecret,
            PreviousStartUtc = PreviousStartUtc,
            CreatedAt = CreatedAtUtc,
        });
        context.SaveChanges();

        var stored = context.FollowUps.AsNoTracking().Single();
        stored.Id.ShouldNotBe(Guid.Empty);
        stored.BookingId.ShouldBe(booking.Id);
        stored.Type.ShouldBe(FollowUpType.Rescheduled);
        stored.Secret.ShouldBe(ManageSecret);
        stored.PreviousStartUtc.ShouldBe(PreviousStartUtc);
        stored.CreatedAt.ShouldBe(CreatedAtUtc);
        stored.Attempts.ShouldBe(NoAttempts);
        stored.LastError.ShouldBeNull();
    }

    /// <summary>
    /// One confirmed meeting on one link. Deliberately the shape
    /// <see cref="Integrations.CalendarSyncTests"/> seeds, because a second way to build a booking
    /// is a second thing to keep true.
    /// </summary>
    private static Booking SeedBooking(KairosDbContext context)
    {
        var pattern = new Pattern { Id = Guid.CreateVersion7(), Name = PatternName };
        var link = new Link
        {
            Id = Guid.CreateVersion7(),
            Slug = Slug,
            Name = LinkName,
            PatternId = pattern.Id,
            Active = true,
        };

        var booking = new Booking
        {
            Id = Guid.CreateVersion7(),
            LinkId = link.Id,
            StartUtc = MeetingStartUtc,
            EndUtc = MeetingEndUtc,
            Notes = string.Empty,
            Status = BookingStatus.Confirmed,
            ManageTokenHash = BookingWriter.Hash(Guid.CreateVersion7().ToString("N")),
            CreatedAt = MeetingStartUtc,
            Participants =
            [
                new BookingParticipant
                {
                    Id = Guid.CreateVersion7(),
                    Role = ParticipantRole.Booker,
                    Name = BookerName,
                    Email = BookerEmail,
                    TimeZoneId = BookerZone,
                },
            ],
        };

        context.Patterns.Add(pattern);
        context.Links.Add(link);
        context.Bookings.Add(booking);
        context.SaveChanges();

        return booking;
    }
}
