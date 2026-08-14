using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kairos.Api.Persistence.Configurations;

public sealed class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
    public void Configure(EntityTypeBuilder<Booking> builder)
    {
        builder.HasKey(booking => booking.Id);

        // Stored as its underlying int, not as text: the column is then constrained to the
        // values the enum defines, and a typo cannot become a new status by accident.
        builder.Property(booking => booking.Status)
            .HasConversion<int>();

        // A link with bookings against it is not deletable. Deactivate it instead.
        builder.HasOne(booking => booking.Link)
            .WithMany()
            .HasForeignKey(booking => booking.LinkId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(booking => new { booking.Status, booking.StartUtc, booking.EndUtc });

        // The manage URL resolves by token hash, so the lookup gets an index rather than a scan.
        builder.HasIndex(booking => booking.ManageTokenHash)
            .IsUnique();

        // Backstop under BEGIN IMMEDIATE, not the mechanism. The filter is built from the enum
        // rather than typed, so renumbering cannot silently orphan the index.
        builder.HasIndex(booking => booking.StartUtc)
            .IsUnique()
            .HasFilter($"Status = {(int)BookingStatus.Confirmed}");
    }
}
