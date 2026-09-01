using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kairos.Api.Persistence.Configurations;

public sealed class ReminderConfiguration : IEntityTypeConfiguration<Reminder>
{
    public void Configure(EntityTypeBuilder<Reminder> builder)
    {
        builder.HasKey(reminder => reminder.Id);

        builder.Property(reminder => reminder.Id)
            .HasValueGenerator<Uuid7ValueGenerator>();

        // Cascade, where Booking's other children Restrict. Those are records of things that
        // happened and outlive the booking on purpose; this is an intention to send a mail, and
        // an intention to remind somebody about a meeting that no longer exists is not one.
        builder.HasOne(reminder => reminder.Booking)
            .WithMany()
            .HasForeignKey(reminder => reminder.BookingId)
            .OnDelete(DeleteBehavior.Cascade);

        // Exactly what the sweep's predicate lands on, in the order it filters.
        builder.HasIndex(reminder => new { reminder.SentAt, reminder.DueAt });
    }
}
