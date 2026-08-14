using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kairos.Api.Persistence.Configurations;

public sealed class PatternConfiguration : IEntityTypeConfiguration<Pattern>
{
    public void Configure(EntityTypeBuilder<Pattern> builder)
    {
        builder.HasKey(pattern => pattern.Id);

        // The name is how the operator picks one in Part C, so two patterns called "Weekdays"
        // is a mistake rather than a choice.
        builder.HasIndex(pattern => pattern.Name)
            .IsUnique();
    }
}
