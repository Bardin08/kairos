using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kairos.Api.Persistence.Configurations;

public sealed class PatternOverrideConfiguration : IEntityTypeConfiguration<PatternOverride>
{
    public void Configure(EntityTypeBuilder<PatternOverride> builder)
    {
        builder.HasKey(patternOverride => patternOverride.Id);

        // Without this EF falls back to its own generator, which is v4 — plausible-looking and
        // wrong in the one way that never raises an error. An explicitly assigned id still wins.
        builder.Property(patternOverride => patternOverride.Id)
            .HasValueGenerator<Uuid7ValueGenerator>();

        builder.HasOne(patternOverride => patternOverride.Pattern)
            .WithMany(pattern => pattern.Overrides)
            .HasForeignKey(patternOverride => patternOverride.PatternId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(patternOverride => new { patternOverride.PatternId, patternOverride.Date });
    }
}
