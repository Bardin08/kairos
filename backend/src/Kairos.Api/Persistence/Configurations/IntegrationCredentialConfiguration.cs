using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kairos.Api.Persistence.Configurations;

public sealed class IntegrationCredentialConfiguration : IEntityTypeConfiguration<IntegrationCredential>
{
    public void Configure(EntityTypeBuilder<IntegrationCredential> builder)
    {
        builder.HasKey(credential => credential.Id);

        // Without this EF falls back to its own generator, which is v4 — plausible-looking and
        // wrong in the one way that never raises an error. An explicitly assigned id still wins.
        builder.Property(credential => credential.Id)
            .HasValueGenerator<Uuid7ValueGenerator>();

        builder.Property(credential => credential.Provider)
            .HasConversion<int>();

        // One connected account per provider — which is what "a single operator" means expressed
        // as a constraint rather than as a rule someone has to remember.
        builder.HasIndex(credential => credential.Provider)
            .IsUnique();
    }
}
