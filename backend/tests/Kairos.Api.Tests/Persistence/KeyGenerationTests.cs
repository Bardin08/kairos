using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;

using Shouldly;

namespace Kairos.Api.Tests.Persistence;

public class KeyGenerationTests
{
    /// <summary>The version nibble of a canonical Guid string sits at index 14.</summary>
    private const int VersionNibbleIndex = 14;

    private const char Uuid7 = '7';

    /// <summary>Enough to cross a millisecond boundary. Thread.Sleep never returns early.</summary>
    private const int MillisecondsBetweenBookings = 2;

    [Fact]
    public void Model_EveryGuidPrimaryKey_MintsUuidV7()
    {
        // The model-level half. A ninth entity added in Part B without the generator would leave
        // its keys on EF's v4 fallback, and nothing else in the suite would notice.
        using var database = new TestDatabase();
        using var context = database.CreateContext();

        // Foreign keys are excluded: LinkDuration's key is (LinkId, Minutes), and LinkId is handed
        // down by the parent Link rather than minted. A generator there would be wrong, not missing.
        var guidKeys = context.Model
            .GetEntityTypes()
            .SelectMany(entityType => entityType.FindPrimaryKey()!.Properties)
            .Where(property => property.ClrType == typeof(Guid) && !property.IsForeignKey())
            .ToList();

        guidKeys.ShouldNotBeEmpty();

        foreach (var property in guidKeys)
        {
            var factory = property.GetValueGeneratorFactory();
            factory.ShouldNotBeNull($"{property.DeclaringType.ShortName()}.{property.Name} has no value generator");

            var generated = (Guid)factory(property, property.DeclaringType).Next(null!)!;
            generated.ToString()[VersionNibbleIndex]
                .ShouldBe(Uuid7, $"{property.DeclaringType.ShortName()}.{property.Name} did not mint a v7");
        }
    }

    [Fact]
    public void Bookings_SavedWithoutExplicitIds_AreStoredInAscendingLexicographicOrder()
    {
        // The end-to-end half, and Task 11's acceptance condition 7. Ids go to SQLite as canonical
        // hyphenated TEXT, so ordering there is lexicographic; only v7's leading timestamp makes
        // that chronological. Ids are left unset on purpose — that is the path that was broken.
        using var database = new TestDatabase();
        using var context = database.CreateContext();

        var pattern = new Pattern { Name = "Weekdays" };
        var link = new Link { Slug = "consulting", Name = "Consulting", Pattern = pattern };
        context.Links.Add(link);
        context.SaveChanges();

        // Saved one at a time: a single SaveChanges would order the inserts itself, which would
        // prove the sort rather than the keys. The sleep is load-bearing — v7 orders by a
        // millisecond timestamp, and .NET's Guid.CreateVersion7 fills the rest with plain random
        // bits rather than a counter, so two ids minted inside one millisecond sort arbitrarily.
        // Sleeping puts each booking in its own millisecond, which is what the clock does anyway
        // between two people booking a call.
        foreach (var offset in Enumerable.Range(0, 5))
        {
            context.Bookings.Add(new Booking
            {
                Link = link,
                StartUtc = 1_800_000_000 + (offset * 3600),
                EndUtc = 1_800_001_800 + (offset * 3600),
                Status = BookingStatus.Confirmed,
                ManageTokenHash = $"hash-{offset}",
            });
            context.SaveChanges();
            Thread.Sleep(MillisecondsBetweenBookings);
        }

        var idsInRowOrder = context.Database
            .SqlQuery<string>($"SELECT Id AS Value FROM Bookings ORDER BY rowid")
            .ToList();

        idsInRowOrder.Count.ShouldBe(5);
        idsInRowOrder.ShouldAllBe(id => id[VersionNibbleIndex] == Uuid7);
        idsInRowOrder.ShouldBe(idsInRowOrder.Order(StringComparer.Ordinal).ToList());
    }
}
