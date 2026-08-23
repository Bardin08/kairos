using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kairos.Api.Persistence.Configurations;

public sealed class PatternConfiguration : IEntityTypeConfiguration<Pattern>
{
    public void Configure(EntityTypeBuilder<Pattern> builder)
    {
        builder.HasKey(pattern => pattern.Id);

        // Without this EF falls back to its own generator, which is v4 — plausible-looking and
        // wrong in the one way that never raises an error. An explicitly assigned id still wins.
        builder.Property(pattern => pattern.Id)
            .HasValueGenerator<Uuid7ValueGenerator>();

        // What makes the override write safe against a second tab. EF puts the value it read
        // into the UPDATE's WHERE clause, so the writer that saves second matches zero rows and
        // raises rather than committing on top of a pattern that moved — and ConcurrentWrite
        // turns that into the named 409. Only a tracked write is bound by it: RenameAsync and
        // DeleteAsync go through ExecuteUpdate and ExecuteDelete, which never load the row and
        // are unaffected, which is why adding this changed no answer they give.
        builder.Property(pattern => pattern.Version)
            .IsConcurrencyToken();

        // The name is how the operator picks one in Part C, so two patterns called "Weekdays"
        // is a mistake rather than a choice.
        builder.HasIndex(pattern => pattern.Name)
            .IsUnique();
    }
}
