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

        // Four columns, not the two this carried before a date was allowed several windows: what
        // must not exist twice is a window, not a date. Two writers interleaving around the
        // delete is the only way one can arise, and the violation is reported as a named 409
        // through ConcurrentWrite rather than pre-checked in a SELECT a second request races.
        //
        // The blocked-date row — both minute columns null — is outside this: SQLite treats nulls
        // as distinct, so the index does not bound it. Nothing needs it to; a request carrying no
        // windows adds exactly one row.
        builder.HasIndex(patternOverride => new
        {
            patternOverride.PatternId,
            patternOverride.Date,
            patternOverride.StartMinute,
            patternOverride.EndMinute,
        }).IsUnique();
    }
}
