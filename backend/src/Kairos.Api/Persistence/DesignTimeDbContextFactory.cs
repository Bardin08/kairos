using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Kairos.Api.Persistence;

/// <summary>
/// Only `dotnet ef` uses this. It exists so generating a migration does not depend on the app's
/// DI container being wired a particular way, and so it never needs a real database: migration
/// scaffolding reads the model, it does not connect. The runtime gets its connection string from
/// configuration and never comes through here.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<KairosDbContext>
{
    private const string PlaceholderConnectionString = "Data Source=design-time.db";

    public KairosDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<KairosDbContext>()
            .UseSqlite(PlaceholderConnectionString)
            .Options;

        return new KairosDbContext(options);
    }
}
