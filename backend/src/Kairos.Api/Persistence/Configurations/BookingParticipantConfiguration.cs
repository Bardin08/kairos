using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kairos.Api.Persistence.Configurations;

public sealed class BookingParticipantConfiguration : IEntityTypeConfiguration<BookingParticipant>
{
    public void Configure(EntityTypeBuilder<BookingParticipant> builder)
    {
        builder.HasKey(participant => participant.Id);

        builder.Property(participant => participant.Id)
            .HasValueGenerator<Uuid7ValueGenerator>();

        builder.Property(participant => participant.Role)
            .HasConversion<int>();

        // Cascade, unlike the Link relationship: a participant has no meaning without the
        // booking it is on, so deleting the booking should not leave the row behind.
        builder.HasOne(participant => participant.Booking)
            .WithMany(booking => booking.Participants)
            .HasForeignKey(participant => participant.BookingId)
            .OnDelete(DeleteBehavior.Cascade);

        // Indexed rather than buried in a JSON column: "find my bookings by email" is the one
        // lookup a person without their manage link has.
        builder.HasIndex(participant => participant.Email);
    }
}
