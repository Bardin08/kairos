using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kairos.Api.Persistence.Configurations;

public sealed class CalendarEntryConfiguration : IEntityTypeConfiguration<CalendarEntry>
{
    public void Configure(EntityTypeBuilder<CalendarEntry> builder)
    {
        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.Id)
            .HasValueGenerator<Uuid7ValueGenerator>();

        builder.Property(entry => entry.Provider)
            .HasConversion<int>();

        builder.Property(entry => entry.State)
            .HasConversion<int>();

        // Cascade: an entry describes a booking's presence in a calendar and means nothing
        // without it.
        builder.HasOne(entry => entry.Booking)
            .WithMany()
            .HasForeignKey(entry => entry.BookingId)
            .OnDelete(DeleteBehavior.Cascade);

        // A booking is in a given provider's calendar once. A retry updates the row it already
        // has rather than writing a second one that also claims to be the event.
        builder.HasIndex(entry => new { entry.BookingId, entry.Provider })
            .IsUnique();
    }
}
