using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kairos.Api.Persistence.Configurations;

public sealed class PersonalInviteConfiguration : IEntityTypeConfiguration<PersonalInvite>
{
    public void Configure(EntityTypeBuilder<PersonalInvite> builder)
    {
        builder.HasKey(invite => invite.Id);

        builder.Property(invite => invite.Id)
            .HasValueGenerator<Uuid7ValueGenerator>();

        // The public route resolves by hash, so it gets an index rather than a scan — and the
        // uniqueness is the guarantee, not the speed.
        builder.HasIndex(invite => invite.TokenHash)
            .IsUnique();

        // A link with invites against it is not deletable, the same answer Booking gives its
        // link. An invite is the record of an offer that was made, and the offer stays made.
        builder.HasOne(invite => invite.Link)
            .WithMany()
            .HasForeignKey(invite => invite.LinkId)
            .OnDelete(DeleteBehavior.Restrict);

        // What the operator's list reads: every invite for one link, newest first.
        builder.HasIndex(invite => new { invite.LinkId, invite.CreatedAt });
    }
}
