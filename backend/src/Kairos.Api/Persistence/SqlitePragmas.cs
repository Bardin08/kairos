using System.Data;

using Microsoft.Data.Sqlite;

namespace Kairos.Api.Persistence;

/// <summary>
/// The four settings SQLite needs before anything depends on them. Retrofitting these after a
/// concurrency bug is how "SQLite cannot handle it" gets concluded wrongly.
/// </summary>
public static class SqlitePragmas
{
    /// <summary>
    /// How long a blocked writer waits for the lock. SQLite defaults to 0, so without this the
    /// first contended write fails instantly rather than waiting for the writer ahead of it.
    /// </summary>
    public const int BusyTimeoutMilliseconds = 5000;

    /// <summary>
    /// Applies the pragmas to one connection, opening it first if it is closed. Must run on every
    /// open: foreign_keys is a per-connection setting, not a property of the database file.
    /// </summary>
    public static void Apply(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.State != ConnectionState.Open)
        {
            connection.Open();
        }

        using var command = connection.CreateCommand();

        // journal_mode is durable in the file and needs a file-backed database; the other three
        // are per-connection. Setting all four together keeps the difference from mattering.
        command.CommandText = $"""
            PRAGMA journal_mode = WAL;
            PRAGMA busy_timeout = {BusyTimeoutMilliseconds};
            PRAGMA foreign_keys = ON;
            PRAGMA synchronous = NORMAL;
            """;
        command.ExecuteNonQuery();
    }
}
