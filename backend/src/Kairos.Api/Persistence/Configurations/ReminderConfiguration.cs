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

        // Cascade: an intention to remind somebody about a meeting that no longer exists is
        // not one. BookingProposalConfiguration is the one sibling that restricts instead,
        // because a proposal is the record of an offer made and the offer stays made.
        builder.HasOne(reminder => reminder.Booking)
            .WithMany()
            .HasForeignKey(reminder => reminder.BookingId)
            .OnDelete(DeleteBehavior.Cascade);

        // Exactly what the sweep's predicate lands on, in the order it filters.
        builder.HasIndex(reminder => new { reminder.SentAt, reminder.DueAt });
    }
}
