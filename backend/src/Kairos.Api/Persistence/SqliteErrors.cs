using Microsoft.Data.Sqlite;

namespace Kairos.Api.Persistence;

/// <summary>
/// Reads what SQLite actually said, through however many layers wrapped it.
/// </summary>
/// <remarks>
/// The outbox is built on a unique index doing the work an application-level "have I already
/// done this?" check would otherwise do, which means two callers now have to recognise the
/// constraint firing and treat it as an answer rather than as a fault. Recognising it is one
/// error number, and one error number written down twice is one of them going stale.
/// </remarks>
public static class SqliteErrors
{
    /// <summary>
    /// SQLITE_CONSTRAINT_UNIQUE. The primary code, 19, covers UNIQUE, FOREIGN KEY, NOT NULL and
    /// CHECK alike, so matching on it would read a missing booking or a null payload as "already
    /// enqueued" and swallow a real bug. Only the extended code means a duplicate.
    /// </summary>
    private const int ConstraintUniqueErrorCode = 2067;

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
}
