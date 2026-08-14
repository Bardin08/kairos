using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kairos.Api.Persistence.Configurations;

public sealed class LinkConfiguration : IEntityTypeConfiguration<Link>
{
    public void Configure(EntityTypeBuilder<Link> builder)
    {
        builder.HasKey(link => link.Id);

        // Every public request arrives as a slug, and two links answering to one slug would make
        // which page you get a matter of row order.
        builder.HasIndex(link => link.Slug)
            .IsUnique();

        // No inverse navigation: nothing queries a pattern for its links. Restrict, because a
        // pattern in use is not deletable — cascading would silently take the links with it.
        builder.HasOne(link => link.Pattern)
            .WithMany()
            .HasForeignKey(link => link.PatternId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
