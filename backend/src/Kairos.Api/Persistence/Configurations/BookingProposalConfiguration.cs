using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kairos.Api.Persistence.Configurations;

public sealed class BookingProposalConfiguration : IEntityTypeConfiguration<BookingProposal>
{
    public void Configure(EntityTypeBuilder<BookingProposal> builder)
    {
        builder.HasKey(proposal => proposal.Id);

        builder.Property(proposal => proposal.Id)
            .HasValueGenerator<Uuid7ValueGenerator>();

        builder.Property(proposal => proposal.State)
            .HasConversion<int>();

        // A booking with proposals against it is not deletable, the same answer Booking gives its
        // link. A proposal is the record of an offer that was made, and the offer stays made.
        builder.HasOne(proposal => proposal.Booking)
            .WithMany()
            .HasForeignKey(proposal => proposal.BookingId)
            .OnDelete(DeleteBehavior.Restrict);

        // One open proposal per meeting. Filtered, so the closed ones pile up harmlessly — a
        // meeting proposed to three times and answered three times is ordinary. The filter is
        // built from the enum rather than typed, so renumbering cannot silently orphan it.
        builder.HasIndex(proposal => proposal.BookingId)
            .IsUnique()
            .HasFilter($"State = {(int)ProposalState.Open}");

        // The invitee's link resolves by token hash, so the lookup gets an index rather than a scan.
        builder.HasIndex(proposal => proposal.TokenHash)
            .IsUnique();

        // What the sweeper's scan and the hold read both land on.
        builder.HasIndex(proposal => new { proposal.State, proposal.ExpiresAt });
    }
}
