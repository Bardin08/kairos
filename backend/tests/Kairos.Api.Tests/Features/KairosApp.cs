using System.Security.Cryptography;

using Kairos.Api.Persistence;
using Kairos.Api.Security;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The application as it ships, over a throwaway SQLite file. Nothing is substituted: these
/// tests are about the wiring — routing, binding, DataAnnotations, the exception handler and the
/// composition root — so replacing any of it would test a program that does not exist.
/// </summary>
/// <remarks>
/// Typed on <see cref="KairosDbContext"/> rather than on Program: the factory only needs a type
/// from the application's assembly to find its entry point, and using one that already exists
/// saves Program.cs a public partial class that has no other reason to be there.
/// </remarks>
public sealed class KairosApp : WebApplicationFactory<KairosDbContext>
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"kairos-api-{Guid.CreateVersion7():N}.db");

    private HttpClient? _client;

    public HttpClient Client => _client ??= CreateClient();

    /// <summary>
    /// The same database the application is using, for a test that needs a second connection to
    /// it — holding a write lock while a request runs is the only way to reach the contention
    /// path from outside.
    /// </summary>
    public string ConnectionString => $"Data Source={_path}";

    /// <summary>Writes through the application's own context, so the schema is the migrated one.</summary>
    public void Seed(Action<KairosDbContext> seed)
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<KairosDbContext>();
        seed(context);
        context.SaveChanges();
    }

    public TResult Read<TResult>(Func<KairosDbContext, TResult> read)
    {
        using var scope = Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<KairosDbContext>());
    }

    /// <summary>
    /// The connection string is overridden through configuration, exactly as an operator would.
    /// The options object is then rebuilt by hand for a reason that has nothing to do with this
    /// application: AddDbContext attaches the host's service provider to the options, EF keys its
    /// internal provider — and therefore its model — on that, and
    /// <see cref="Kairos.Api.Availability.AvailabilityQuery"/>'s compiled queries are static and
    /// bind to the first model they meet. Two models in one test process means whichever suite
    /// runs second fails with "executed with a different model than it was compiled against".
    /// Built this way the model is the one <see cref="TestDatabase"/> already uses, so the
    /// process has exactly one. Nothing about the composition root is replaced — same context,
    /// same provider, same connection string.
    /// </summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var connectionString = $"Data Source={_path}";

        builder.UseSetting("ConnectionStrings:Db", connectionString);

        // The application refuses to start without a key, so the harness supplies one the same
        // way an operator does. A throwaway key generated per run, because nothing in these
        // tests reads a value written by an earlier one.
        builder.UseSetting(
            "Security:SecretKey",
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretProtector.KeyBytes)));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<KairosDbContext>>();
            services.AddSingleton(
                new DbContextOptionsBuilder<KairosDbContext>().UseSqlite(connectionString).Options);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        _client?.Dispose();

        // The pool holds handles to the file, so clearing it first is what makes the delete
        // succeed rather than leave a temp database behind on every run.
        SqliteConnection.ClearAllPools();

        string[] files = [_path, $"{_path}-wal", $"{_path}-shm"];
        foreach (var file in files)
        {
            File.Delete(file);
        }
    }
}
