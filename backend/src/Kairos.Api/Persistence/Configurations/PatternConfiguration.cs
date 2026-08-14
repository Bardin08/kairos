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

        // The name is how the operator picks one in Part C, so two patterns called "Weekdays"
        // is a mistake rather than a choice.
        builder.HasIndex(pattern => pattern.Name)
            .IsUnique();
    }
}
