using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kairos.Api.Persistence.Configurations;

public sealed class PatternRuleConfiguration : IEntityTypeConfiguration<PatternRule>
{
    public void Configure(EntityTypeBuilder<PatternRule> builder)
    {
        builder.HasKey(rule => rule.Id);

        builder.Property(rule => rule.Id)
            .HasValueGenerator<Uuid7ValueGenerator>();

        builder.HasOne(rule => rule.Pattern)
            .WithMany(pattern => pattern.Rules)
            .HasForeignKey(rule => rule.PatternId)
            .OnDelete(DeleteBehavior.Cascade);

        // Availability is read one weekday at a time, and always for one pattern.
        builder.HasIndex(rule => new { rule.PatternId, rule.IsoDayOfWeek });
    }
}
