using Kairos.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kairos.Api.Tests;

/// <summary>
/// A throwaway SQLite database on disk, one per instance. File-backed rather than ":memory:"
/// because WAL — one of the four pragmas the app depends on — is silently ignored on an in-memory
/// database, so an in-memory fixture would test a configuration that never ships.
/// </summary>
public sealed class TestDatabase : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kairos-test-{Guid.CreateVersion7():N}.db");

    /// <summary>
    /// The same database, for a test that has to build its own container around it rather than
    /// take a context from here — a hosted service resolves its context from a scope factory, so
    /// it needs somewhere to resolve one from.
    /// </summary>
    public string ConnectionString => $"Data Source={_path}";

    /// <summary>
    /// A context on this database, schema applied. Migrate rather than EnsureCreated, so the
    /// tests run against the migration that ships instead of a schema built straight from the
    /// model — drift between the two is exactly the failure worth catching.
    /// </summary>
    public KairosDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<KairosDbContext>()
            .UseSqlite(ConnectionString)
            .Options;

        var context = new KairosDbContext(options);
        context.Database.Migrate();
        return context;
    }

    public void Dispose()
    {
        // The pool keeps handles to the file open, so clearing it first is what makes the delete
        // succeed rather than leave a temp file behind on every run.
        SqliteConnection.ClearAllPools();

        string[] files = [_path, $"{_path}-wal", $"{_path}-shm"];
        foreach (var file in files)
        {
            File.Delete(file);
        }
    }
}
