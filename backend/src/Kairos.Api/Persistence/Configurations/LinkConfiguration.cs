using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kairos.Api.Persistence.Configurations;

public sealed class LinkConfiguration : IEntityTypeConfiguration<Link>
{
    public void Configure(EntityTypeBuilder<Link> builder)
    {
        builder.HasKey(link => link.Id);

        builder.Property(link => link.Id)
            .HasValueGenerator<Uuid7ValueGenerator>();

        // Every public request arrives as a slug, and two links answering to one slug would make
        // which page you get a matter of row order.
        builder.HasIndex(link => link.Slug)
            .IsUnique();

        // No inverse navigation: nothing queries a pattern for its links. Restrict, because a
        // pattern in use is not deletable — cascading would silently take the links with it.
        builder.HasOne(link => link.Pattern)
            .WithMany()
            .HasForeignKey(link => link.PatternId)
            .OnDelete(DeleteBehavior.Restrict);

        // Same floor as Settings, same reason: a negative margin inverts the padded interval and
        // silently un-blocks every busy period. "IS NULL OR" is spelled out rather than leaning on
        // SQLite passing a CHECK whose result is NULL — the reader should not have to know that
        // rule to see that null still means inherit.
        builder.ToTable(table =>
        {
            table.HasCheckConstraint("CK_Links_MarginMinutes_NonNegative", "MarginMinutes IS NULL OR MarginMinutes >= 0");
            table.HasCheckConstraint("CK_Links_MinNoticeMinutes_NonNegative", "MinNoticeMinutes IS NULL OR MinNoticeMinutes >= 0");
            table.HasCheckConstraint("CK_Links_DailyCap_NonNegative", "DailyCap IS NULL OR DailyCap >= 0");
            table.HasCheckConstraint("CK_Links_StartIncrementMinutes_NonNegative", "StartIncrementMinutes IS NULL OR StartIncrementMinutes >= 0");
        });

        // A link must offer at least one duration — Durations.Default is durations[0] and throws
        // on an empty list. Deliberately not enforced here, and not for want of a way.
        //
        // Nothing cheap reaches it: a CHECK cannot span two tables, the foreign key puts the parent
        // row in first, and SQLite's DEFERRABLE covers foreign keys only. What works is the
        // mandatory-one trick — a NOT NULL DefaultDurationMinutes column with a circular composite
        // foreign key to LinkDurations(LinkId, Minutes), DEFERRABLE INITIALLY DEFERRED — costing a
        // column and a hand-edited migration to buy a rule the writer checks anyway.
        //
        // So the invariant is the writer's: reject a create or update leaving zero durations.
    }
}
