using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kairos.Api.Persistence.Configurations;

public sealed class LinkDurationConfiguration : IEntityTypeConfiguration<LinkDuration>
{
    public void Configure(EntityTypeBuilder<LinkDuration> builder)
    {
        // The pair is the identity. A surrogate key would let the same length be offered twice.
        builder.HasKey(duration => new { duration.LinkId, duration.Minutes });

        builder.HasOne(duration => duration.Link)
            .WithMany(link => link.Durations)
            .HasForeignKey(duration => duration.LinkId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
