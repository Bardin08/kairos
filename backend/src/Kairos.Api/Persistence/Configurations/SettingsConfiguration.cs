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
        });
    }
}
