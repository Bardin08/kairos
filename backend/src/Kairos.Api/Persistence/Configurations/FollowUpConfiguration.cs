using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kairos.Api.Persistence.Configurations;

public sealed class FollowUpConfiguration : IEntityTypeConfiguration<FollowUp>
{
    public void Configure(EntityTypeBuilder<FollowUp> builder)
    {
        builder.HasKey(followUp => followUp.Id);

        builder.Property(followUp => followUp.Id)
            .HasValueGenerator<Uuid7ValueGenerator>();

        builder.Property(followUp => followUp.Type)
            .HasConversion<int>();

        builder.HasOne(followUp => followUp.Booking)
            .WithMany()
            .HasForeignKey(followUp => followUp.BookingId)
            .OnDelete(DeleteBehavior.Cascade);

        // No unique index over (BookingId, Type), which is where this parts company with
        // OutboxMessageConfiguration: a meeting can be rescheduled twice, and each move owes its
        // own mail. There is nothing here for a constraint to deduplicate.
        //
        // The worker's queue filters on Attempts < cap, a range, so a leading Attempts column
        // narrows the scan but does not hand back CreatedAt/Id order for free — SQLite still
        // sorts what the range returns. That sort is over rows still in the queue, which drains
        // within seconds of a booking and sits empty the rest of the time, so it is cheap enough
        // that indexing (CreatedAt, Id) for a free-ordered scan instead would be sizing this for
        // a sort that never has more than a handful of rows to do.
        builder.HasIndex(followUp => new
        {
            followUp.Attempts,
            followUp.CreatedAt,
            followUp.Id,
        });
    }
}
