using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kairos.Api.Persistence.Configurations;

public sealed class IntegrationSettingConfiguration : IEntityTypeConfiguration<IntegrationSetting>
{
    public void Configure(EntityTypeBuilder<IntegrationSetting> builder)
    {
        builder.HasKey(setting => setting.Id);

        builder.Property(setting => setting.Id)
            .HasValueGenerator<Uuid7ValueGenerator>();

        builder.Property(setting => setting.Provider)
            .HasConversion<int>();

        builder.HasIndex(setting => setting.Provider)
            .IsUnique();
    }
}
