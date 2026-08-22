using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;

using Shouldly;

namespace Kairos.Api.Tests.Persistence;

public class SchemaTests
{
    private const int MinutesPerHour = 60;

    /// <summary>Minutes from local midnight, which is what the column stores.</summary>
    private const int NineAm = 9 * MinutesPerHour;
    private const int FivePm = 17 * MinutesPerHour;

    [Fact]
    public void Link_WithSeveralDurations_RoundTripsInSortOrder()
    {
        using var database = new TestDatabase();
        var linkId = Guid.CreateVersion7();

        using (var context = database.CreateContext())
        {
            var pattern = new Pattern { Id = Guid.CreateVersion7(), Name = "Weekdays" };
            context.Patterns.Add(pattern);
            context.Links.Add(new Link
            {
                Id = linkId,
                Slug = "consulting",
                Name = "Consulting",
                PatternId = pattern.Id,
                Durations =
                [
                    new LinkDuration { Minutes = 30, SortOrder = 0 },
                    new LinkDuration { Minutes = 25, SortOrder = 1 }
                ]
            });
            context.SaveChanges();
        }

        using var read = database.CreateContext();
        read.Links
            .AsNoTracking()
            .Include(link => link.Durations)
            .Single()
            .Durations
            .OrderBy(duration => duration.SortOrder)
            .Select(duration => duration.Minutes)
            .ShouldBe([30, 25]);
    }

    [Fact]
    public void SaveChanges_ParticipantWithoutABooking_IsRejectedByTheForeignKey()
    {
        // Proves PRAGMA foreign_keys reached this connection. SQLite ignores foreign keys
        // silently when it is off, so this asserts enforcement rather than declaration.
        using var database = new TestDatabase();
        using var context = database.CreateContext();

        context.BookingParticipants.Add(new BookingParticipant
        {
            Id = Guid.CreateVersion7(),
            BookingId = Guid.CreateVersion7(),   // no such booking
            Role = ParticipantRole.Booker,
            Name = "Nobody",
            Email = "nobody@example.com",
        });

        Should.Throw<DbUpdateException>(() => context.SaveChanges());
    }

    /// <summary>
    /// The assumption <see cref="Kairos.Api.Shared.ConcurrentWrite"/> stands on: two writers that
    /// both read a date's rows before either saves, which is two admin tabs on one date. The
    /// second delete finds nothing to delete and EF raises rather than writing a merged set. If a
    /// provider change ever made this silent, the 409 above would become a lie and nothing else
    /// would notice.
    /// </summary>
    [Fact]
    public void SaveChanges_WhenAnotherWriterAlreadyReplacedTheDate_RaisesConcurrency()
    {
        using var database = new TestDatabase();
        var patternId = Guid.CreateVersion7();
        var date = new DateOnly(2026, 9, 14);

        using (var seed = database.CreateContext())
        {
            seed.Patterns.Add(new Pattern { Id = patternId, Name = "Weekdays" });
            seed.PatternOverrides.Add(new PatternOverride
            {
                Id = Guid.CreateVersion7(),
                PatternId = patternId,
                Date = date,
                StartMinute = NineAm,
                EndMinute = FivePm,
            });
            seed.SaveChanges();
        }

        // Both read before either writes, which is the whole of the race.
        using var first = database.CreateContext();
        using var second = database.CreateContext();
        var firstRows = first.PatternOverrides.Where(row => row.Date == date).ToList();
        var secondRows = second.PatternOverrides.Where(row => row.Date == date).ToList();

        first.PatternOverrides.RemoveRange(firstRows);
        first.SaveChanges();

        second.PatternOverrides.RemoveRange(secondRows);
        Should.Throw<DbUpdateConcurrencyException>(() => second.SaveChanges());
    }
}
