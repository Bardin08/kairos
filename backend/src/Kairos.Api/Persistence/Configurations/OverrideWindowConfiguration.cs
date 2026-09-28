using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kairos.Api.Persistence.Configurations;

public sealed class OverrideWindowConfiguration : IEntityTypeConfiguration<OverrideWindow>
{
    public void Configure(EntityTypeBuilder<OverrideWindow> builder)
    {
        builder.HasKey(window => window.Id);

        builder.Property(window => window.Id)
            .HasValueGenerator<Uuid7ValueGenerator>();

        // RemoveOverrideAsync deletes the date with ExecuteDelete and leaves its windows to this
        // cascade, which runs because SqlitePragmas turns foreign_keys on for every connection.
        builder.HasOne(window => window.PatternOverride)
            .WithMany(patternOverride => patternOverride.Windows)
            .HasForeignKey(window => window.PatternOverrideId)
            .OnDelete(DeleteBehavior.Cascade);

        // What must not exist twice is a window on a date. Only writers interleaving around the
        // delete can produce one, and the violation comes back as a named 409 through
        // ConcurrentWrite rather than a pre-check a second request races.
        builder.HasIndex(window => new { window.PatternOverrideId, window.StartMinute, window.EndMinute })
            .IsUnique();
    }
}
