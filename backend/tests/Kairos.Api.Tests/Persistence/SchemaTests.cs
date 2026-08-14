using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;

using Shouldly;

namespace Kairos.Api.Tests.Persistence;

public class SchemaTests
{
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
}
