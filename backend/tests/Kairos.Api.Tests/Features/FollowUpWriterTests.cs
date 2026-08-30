using Kairos.Api.Features.Bookings;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Security;

using Microsoft.EntityFrameworkCore;

using NodaTime;

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

    private const string ManageToken = "8f14e45fceea167a5a36dedd4bea2543";

    /// <summary>
    /// Thirty-two zero bytes, base64. A key a test can name, and one no deployment would ever
    /// hold — <see cref="SecretProtector"/> only checks the length.
    /// </summary>
    private static readonly string TestKey = Convert.ToBase64String(new byte[SecretProtector.KeyBytes]);

    private static readonly Instant Now = Instant.FromUnixTimeSeconds(CreatedAtUtc);

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
    /// The property the whole design turns on: what reaches disk is ciphertext, and it opens back
    /// to the token the mailer would have been handed inline.
    /// </summary>
    [Fact]
    public void Enqueue_WithAManageToken_StoresItEncryptedAndRecoverable()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = SeedBooking(context);
        var protector = new SecretProtector(TestKey);

        new FollowUpWriter(context, protector, new FixedClock(Now))
            .Enqueue(booking.Id, FollowUpType.Created, ManageToken);
        context.SaveChanges();

        var stored = context.FollowUps.AsNoTracking().Single();
        stored.Secret.ShouldNotBeNull();
        stored.Secret.ShouldNotContain(ManageToken);
        protector.Unprotect(stored.Secret).ShouldBe(ManageToken);
    }

    /// <summary>
    /// The operator's cancel has no token to carry, and null must stay null rather than become
    /// the ciphertext of an empty string — which would decrypt to "" and print an empty link.
    /// </summary>
    [Fact]
    public void Enqueue_WithoutAManageToken_StoresNoSecret()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = SeedBooking(context);

        new FollowUpWriter(context, new SecretProtector(TestKey), new FixedClock(Now))
            .Enqueue(booking.Id, FollowUpType.Cancelled);
        context.SaveChanges();

        context.FollowUps.AsNoTracking().Single().Secret.ShouldBeNull();
    }

    /// <summary>
    /// The outbox discipline, restated for this table: the writer adds and never saves, so a
    /// caller whose own write fails leaves no row behind promising a mail about it.
    /// </summary>
    [Fact]
    public void Enqueue_WithoutASave_WritesNothing()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = SeedBooking(context);

        new FollowUpWriter(context, new SecretProtector(TestKey), new FixedClock(Now))
            .Enqueue(booking.Id, FollowUpType.Created, ManageToken);

        // Its own connection rather than database.CreateContext(), which migrates on every call:
        // a second concurrent migration against the same SQLite file this test's subject is
        // mid-write to is a way to find out about locking, not about what got saved.
        using var reader = new KairosDbContext(
            new DbContextOptionsBuilder<KairosDbContext>().UseSqlite(database.ConnectionString).Options);
        reader.FollowUps.AsNoTracking().ShouldBeEmpty();
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
