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

        // Four columns, not two: what must not exist twice is a window, not a date. Only writers
        // interleaving around the delete can produce one, and the violation comes back as a named
        // 409 through ConcurrentWrite rather than a pre-check a second request races.
        //
        // The blocked-date row — both minute columns null — is outside this, SQLite treating nulls
        // as distinct. Nothing needs it bounded: a request with no windows adds one row.
        builder.HasIndex(patternOverride => new
        {
            patternOverride.PatternId,
            patternOverride.Date,
            patternOverride.StartMinute,
            patternOverride.EndMinute,
        }).IsUnique();
    }
}
