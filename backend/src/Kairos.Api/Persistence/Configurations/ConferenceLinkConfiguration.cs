using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kairos.Api.Persistence.Configurations;

public sealed class ConferenceLinkConfiguration : IEntityTypeConfiguration<ConferenceLink>
{
    public void Configure(EntityTypeBuilder<ConferenceLink> builder)
    {
        builder.HasKey(link => link.Id);

        // Without this EF falls back to its own generator, which is v4 — plausible-looking and
        // wrong in the one way that never raises an error. An explicitly assigned id still wins.
        builder.Property(link => link.Id)
            .HasValueGenerator<Uuid7ValueGenerator>();

        builder.Property(link => link.Provider)
            .HasConversion<int>();

        builder.HasOne(link => link.Booking)
            .WithMany()
            .HasForeignKey(link => link.BookingId)
            .OnDelete(DeleteBehavior.Cascade);

        // One join URL per booking per provider, for the same reason as CalendarEntry: a retry
        // must not leave two links that both claim to be the meeting.
        builder.HasIndex(link => new { link.BookingId, link.Provider })
            .IsUnique();
    }
}
