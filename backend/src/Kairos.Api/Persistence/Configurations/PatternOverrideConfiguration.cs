using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kairos.Api.Persistence.Configurations;

public sealed class PatternOverrideConfiguration : IEntityTypeConfiguration<PatternOverride>
{
    public void Configure(EntityTypeBuilder<PatternOverride> builder)
    {
        builder.HasKey(patternOverride => patternOverride.Id);

        builder.HasOne(patternOverride => patternOverride.Pattern)
            .WithMany(pattern => pattern.Overrides)
            .HasForeignKey(patternOverride => patternOverride.PatternId)
            .OnDelete(DeleteBehavior.Cascade);

        // An override replaces the day, so two of them for one date is a contradiction the
        // database should refuse rather than a precedence rule the code has to invent.
        builder.HasIndex(patternOverride => new { patternOverride.PatternId, patternOverride.Date })
            .IsUnique();
    }
}
