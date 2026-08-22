using System.Globalization;
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
/// <param name="webRoot">
/// A directory to serve static files from, for the one test that needs an <c>index.html</c> to
/// exist. The single page is built by the other CI job and never by this one, so the web root is
/// empty while these tests run — and a test about the single-page fallback would then pass whether
/// or not the route that makes it true is there. Left null, nothing changes.
/// </param>
public sealed class KairosApp(string? webRoot = null) : WebApplicationFactory<KairosDbContext>
{
    /// <summary>Far above anything one test does, and finite so a runaway loop still stops.</summary>
    private const int TestPermits = 100_000;

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

        if (webRoot is not null)
        {
            builder.UseWebRoot(webRoot);
        }

        // The application refuses to start without a key, so the harness supplies one the same
        // way an operator does. A throwaway key generated per run, because nothing in these
        // tests reads a value written by an earlier one.
        builder.UseSetting(
            "Security:SecretKey",
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretProtector.KeyBytes)));

        // Same rule for Pocket ID: the application refuses to start without an issuer and a
        // client, so the harness supplies them. Nothing here is ever contacted — a request that
        // has no session is refused before anything is challenged, and that is the only side of
        // the gate these tests are on.
        builder.UseSetting("Authentication:Authority", "https://id.kairos.test");
        builder.UseSetting("Authentication:ClientId", "kairos-tests");
        builder.UseSetting("Authentication:ClientSecret", "kairos-tests");

        // Every other suite makes far more than ten requests through one partition — the test
        // host has no remote address, so they all share one — and none of them is about
        // throttling. The suite that IS about throttling sets the shipping numbers back.
        builder.UseSetting(
            $"{RateLimitOptions.SectionName}:{nameof(RateLimitOptions.WritePermits)}",
            TestPermits.ToString(CultureInfo.InvariantCulture));
        builder.UseSetting(
            $"{RateLimitOptions.SectionName}:{nameof(RateLimitOptions.ReadPermits)}",
            TestPermits.ToString(CultureInfo.InvariantCulture));

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
        // succeed rather than leave a temp database behind on every run. Scoped to this database's
        // own pool: ClearAllPools is process-global, and with collections running in parallel it
        // tears down connections belonging to every other test still in flight, which surfaces as
        // a disposed sqlite3 handle in whichever unlucky sibling opens one next.
        SqliteConnection.ClearPool(new SqliteConnection(ConnectionString));

        string[] files = [_path, $"{_path}-wal", $"{_path}-shm"];
        foreach (var file in files)
        {
            File.Delete(file);
        }
    }
}
