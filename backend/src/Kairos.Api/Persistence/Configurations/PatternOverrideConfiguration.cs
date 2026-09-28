using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kairos.Api.Persistence.Configurations;

public sealed class PatternOverrideConfiguration : IEntityTypeConfiguration<PatternOverride>
{
    public void Configure(EntityTypeBuilder<PatternOverride> builder)
    {
        builder.HasKey(patternOverride => patternOverride.Id);

        builder.Property(patternOverride => patternOverride.Id)
            .HasValueGenerator<Uuid7ValueGenerator>();

        builder.HasOne(patternOverride => patternOverride.Pattern)
            .WithMany(pattern => pattern.Overrides)
            .HasForeignKey(patternOverride => patternOverride.PatternId)
            .OnDelete(DeleteBehavior.Cascade);

        // One row per date is what lets a row with no windows mean blocked. A second row for the
        // same date would be a second answer to one question, and no reader chooses between them.
        builder.HasIndex(patternOverride => new { patternOverride.PatternId, patternOverride.Date })
            .IsUnique();
    }
}
