using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kairos.Api.Persistence.Configurations;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.HasKey(message => message.Id);

        // Without this EF falls back to its own generator, which is v4 — plausible-looking and
        // wrong in the one way that never raises an error. An explicitly assigned id still wins.
        builder.Property(message => message.Id)
            .HasValueGenerator<Uuid7ValueGenerator>();

        builder.Property(message => message.Type)
            .HasConversion<int>();

        builder.HasOne(message => message.Booking)
            .WithMany()
            .HasForeignKey(message => message.BookingId)
            .OnDelete(DeleteBehavior.Cascade);

        // This is why MeetingEndedPoller needs no "have I already enqueued this?" query: it
        // enqueues, and a duplicate insert violates the constraint and is swallowed. A schema
        // guarantee beats a check-then-act — the same reasoning that put BEGIN IMMEDIATE in
        // Part A rather than an application-level lock.
        builder.HasIndex(message => new { message.BookingId, message.Type })
            .IsUnique();

        // The dispatcher's queue is "everything still null here", so it reads an index rather
        // than scanning a table that only ever grows.
        builder.HasIndex(message => message.DispatchedAt);
    }
}
