using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kairos.Api.Persistence.Configurations;

public sealed class PatternRuleConfiguration : IEntityTypeConfiguration<PatternRule>
{
    public void Configure(EntityTypeBuilder<PatternRule> builder)
    {
        builder.HasKey(rule => rule.Id);

        // Without this EF falls back to its own generator, which is v4 — plausible-looking and
        // wrong in the one way that never raises an error. An explicitly assigned id still wins.
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
