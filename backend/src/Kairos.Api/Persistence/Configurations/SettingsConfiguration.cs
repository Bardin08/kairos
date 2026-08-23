using Kairos.Api.Persistence.Entities;
using Kairos.Api.Scheduling;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kairos.Api.Persistence.Configurations;

public sealed class SettingsConfiguration : IEntityTypeConfiguration<Settings>
{
    public void Configure(EntityTypeBuilder<Settings> builder)
    {
        builder.HasKey(settings => settings.Id);

        // Fixed rather than generated: a second insert collides on the primary key instead of
        // quietly creating a rival configuration nothing reads.
        builder.Property(settings => settings.Id)
            .ValueGeneratedNever();

        // A negative limit is not a smaller limit — it inverts the padded interval, so a busy
        // block stops overlapping anything and the booking page hands out a taken slot with no
        // exception and no log line. The kernel does no floor check, so the floor lives here,
        // one constraint per column so a violation names the column that broke it.
        //
        // The start increment's floor is one and not zero, which is the one place these four
        // differ. Zero is not a smaller step, it is no step at all: the generator advances by
        // local.PlusMinutes(step), so a zero never moves and the loop stops producing starts.
        // Part A defends against it inside the loop with Math.Max(step, 1); this is the same
        // invariant at the only level that can still be written past an endpoint, and it agrees
        // with the [Range(1, …)] a link's own override has answered to since Task 3.
        builder.ToTable(table =>
        {
            table.HasCheckConstraint("CK_Settings_MarginMinutes_NonNegative", "MarginMinutes >= 0");
            table.HasCheckConstraint("CK_Settings_MinNoticeMinutes_NonNegative", "MinNoticeMinutes >= 0");
            table.HasCheckConstraint("CK_Settings_DailyCap_NonNegative", "DailyCap >= 0");
            table.HasCheckConstraint("CK_Settings_StartIncrementMinutes_AtLeastOne", "StartIncrementMinutes >= 1");

            // Zero hours is not a shorter hold, it is a proposal that has lapsed before the mail
            // carrying it has been composed. Same shape as the start increment's floor of one.
            table.HasCheckConstraint("CK_Settings_ProposalHoldHours_AtLeastOne", "ProposalHoldHours >= 1");
        });

        // The only HasData in the model. The numbers live in SchedulingDefaults so the seed and
        // the kernel cannot drift apart.
        builder.HasData(new Settings
        {
            Id = Settings.SingletonId,
            TimeZoneId = SchedulingDefaults.TimeZoneId,
            MarginMinutes = SchedulingDefaults.MarginMinutes,
            MinNoticeMinutes = SchedulingDefaults.MinNoticeMinutes,
            DailyCap = SchedulingDefaults.DailyCap,
            StartIncrementMinutes = SchedulingDefaults.StartIncrementMinutes,
            ProposalHoldHours = SchedulingDefaults.ProposalHoldHours,
        });
    }
}
