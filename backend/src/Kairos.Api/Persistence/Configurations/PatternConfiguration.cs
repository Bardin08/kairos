using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kairos.Api.Persistence.Configurations;

public sealed class PatternConfiguration : IEntityTypeConfiguration<Pattern>
{
    public void Configure(EntityTypeBuilder<Pattern> builder)
    {
        builder.HasKey(pattern => pattern.Id);

        builder.Property(pattern => pattern.Id)
            .HasValueGenerator<Uuid7ValueGenerator>();

        // What makes the override write safe against a second tab. EF puts the value it read into
        // the UPDATE's WHERE clause, so the second writer matches zero rows and raises instead of
        // committing on top of a pattern that moved; ConcurrentWrite turns that into the named 409.
        // Only tracked writes are bound: RenameAsync and DeleteAsync go through ExecuteUpdate and
        // ExecuteDelete, which never load the row. RulesVersion deliberately does NOT get the same
        // call — see Pattern.RulesVersion.
        builder.Property(pattern => pattern.OverridesVersion)
            .IsConcurrencyToken();

        // The name is how the operator picks one, so two patterns called "Weekdays" is a mistake
        // rather than a choice.
        builder.HasIndex(pattern => pattern.Name)
            .IsUnique();
    }
}
