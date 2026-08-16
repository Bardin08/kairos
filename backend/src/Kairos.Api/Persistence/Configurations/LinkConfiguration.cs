using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kairos.Api.Persistence.Configurations;

public sealed class LinkConfiguration : IEntityTypeConfiguration<Link>
{
    public void Configure(EntityTypeBuilder<Link> builder)
    {
        builder.HasKey(link => link.Id);

        // Without this EF falls back to its own generator, which is v4 — plausible-looking and
        // wrong in the one way that never raises an error. An explicitly assigned id still wins.
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

        // Same floor as Settings, and for the same reason: a negative margin inverts the padded
        // interval and silently un-blocks every busy period. "IS NULL OR" is spelled out rather
        // than leaning on SQLite passing a CHECK whose result is NULL — the reader should not
        // have to know that rule to see that null still means inherit.
        builder.ToTable(table =>
        {
            table.HasCheckConstraint("CK_Links_MarginMinutes_NonNegative", "MarginMinutes IS NULL OR MarginMinutes >= 0");
            table.HasCheckConstraint("CK_Links_MinNoticeMinutes_NonNegative", "MinNoticeMinutes IS NULL OR MinNoticeMinutes >= 0");
            table.HasCheckConstraint("CK_Links_DailyCap_NonNegative", "DailyCap IS NULL OR DailyCap >= 0");
            table.HasCheckConstraint("CK_Links_StartIncrementMinutes_NonNegative", "StartIncrementMinutes IS NULL OR StartIncrementMinutes >= 0");
        });

        // A link must offer at least one duration — Durations.Default is durations[0] and throws
        // on an empty list, which Task 7 hits on every public booking-page load. That rule is
        // deliberately not enforced here, which is not the same as it being inexpressible.
        //
        // Nothing cheap reaches it: a CHECK cannot span two tables, and the parent row is
        // necessarily inserted before any child because the foreign key requires it, so any
        // immediate constraint would reject the very insert that creates the link. SQLite's
        // DEFERRABLE applies to foreign keys only, so there is no deferred CHECK to postpone to
        // commit. What would work is the mandatory-one trick — a NOT NULL DefaultDurationMinutes
        // column on Links carrying a circular composite foreign key to LinkDurations(LinkId,
        // Minutes), declared DEFERRABLE INITIALLY DEFERRED so the cycle resolves at commit. It
        // costs a column and a hand-edited migration, because EF's SQLite provider does not emit
        // DEFERRABLE, and it buys a rule the writer has to check anyway.
        //
        // So the invariant belongs to whoever writes links: Task 8's handler must reject a create
        // or update that leaves a link with zero durations.
    }
}
