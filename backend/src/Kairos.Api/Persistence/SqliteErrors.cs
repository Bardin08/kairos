using Microsoft.Data.Sqlite;

namespace Kairos.Api.Persistence;

/// <summary>Reads what SQLite actually said, through however many layers wrapped it.</summary>
/// <remarks>
/// The outbox leans on a unique index instead of an application-level "have I already done this?",
/// so two callers must read the constraint firing as an answer rather than a fault. SQLITE_BUSY has
/// two readers of its own, the busy handler and the readiness check, which answer it opposite ways.
/// One error number written down twice is one of them going stale.
/// </remarks>
public static class SqliteErrors
{
    /// <summary>
    /// SQLITE_CONSTRAINT_UNIQUE. The primary code, 19, covers UNIQUE, FOREIGN KEY, NOT NULL and
    /// CHECK alike, so matching on it would read a missing booking or a null payload as "already
    /// enqueued" and swallow a real bug. Only the extended code means a duplicate.
    /// </summary>
    private const int ConstraintUniqueErrorCode = 2067;

    /// <summary>SQLITE_BUSY. The primary code, so every extended form of it is included.</summary>
    private const int BusyErrorCode = 5;

    /// <summary>
    /// Down the chain rather than at the top: EF wraps a failed SaveChanges in DbUpdateException,
    /// and the code worth reading is on the SqliteException underneath it.
    /// </summary>
    public static bool IsUniqueViolation(Exception exception)
    {
        for (var candidate = exception; candidate is not null; candidate = candidate.InnerException)
        {
            if (candidate is SqliteException { SqliteExtendedErrorCode: ConstraintUniqueErrorCode })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Down the chain for the same reason, because EF wraps a failed SaveChanges. Matched on
    /// SQLITE_BUSY alone: every other SQLite error — a constraint the model should have prevented,
    /// a database that is no longer there — is a bug, and a bug is not a queue.
    /// </summary>
    public static bool IsDatabaseBusy(Exception exception)
    {
        for (var candidate = exception; candidate is not null; candidate = candidate.InnerException)
        {
            if (candidate is SqliteException { SqliteErrorCode: BusyErrorCode })
            {
                return true;
            }
        }

        return false;
    }
}
