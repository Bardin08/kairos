using System.Globalization;

using Kairos.Api.Persistence;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace Kairos.Api.Shared;

/// <summary>
/// SQLITE_BUSY as what it actually is: not a fault, a queue. A write that never got the lock
/// inside <see cref="SqlitePragmas.BusyTimeoutMilliseconds"/> has nothing wrong with it — the
/// same request a moment later succeeds — so it answers 503 with a Retry-After rather than a 500
/// that says the server is broken and gives the caller nothing to do about it.
/// </summary>
/// <remarks>
/// A separate handler, registered ahead of <see cref="DomainExceptionHandler"/>, rather than a
/// second branch inside it or a catch at the call site. Inside it would mean that handler
/// answering for something that is not a domain concept, and its refusal to touch anything but a
/// <see cref="DomainException"/> is the property that keeps a bug a 500. At the call site it
/// would mean a failure branch in an endpoint written to have none — three of them, since every
/// write can contend, not just the create.
/// </remarks>
public sealed class DatabaseBusyExceptionHandler(IProblemDetailsService problemDetails)
    : IExceptionHandler
{
    public const string DatabaseBusyCode = "database_busy";

    /// <summary>SQLITE_BUSY. The primary code, so every extended form of it is included.</summary>
    private const int SqliteBusyErrorCode = 5;

    private const int MillisecondsPerSecond = 1000;

    /// <summary>
    /// As long as this server was itself prepared to wait for the lock. Derived rather than
    /// chosen, so retuning the pragma cannot leave the two telling different stories.
    /// </summary>
    private static readonly string RetryAfterSeconds =
        (SqlitePragmas.BusyTimeoutMilliseconds / MillisecondsPerSecond)
        .ToString(CultureInfo.InvariantCulture);

    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (!IsDatabaseBusy(exception))
        {
            return false;
        }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = RetryAfterSeconds;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "The database was busy. Try again.",
                Extensions = { [ProblemCode.Key] = DatabaseBusyCode },
            },
        });
    }

    /// <summary>
    /// Down the chain, because EF wraps a failed SaveChanges in DbUpdateException and the code
    /// worth reading is on the SqliteException underneath it. Matched on SQLITE_BUSY alone: every
    /// other SQLite error — a constraint the model should have prevented, a closed database — is
    /// a bug, and a bug goes on to the 500 and the stack trace that comes with it.
    /// </summary>
    private static bool IsDatabaseBusy(Exception exception)
    {
        for (var candidate = exception; candidate is not null; candidate = candidate.InnerException)
        {
            if (candidate is SqliteException { SqliteErrorCode: SqliteBusyErrorCode })
            {
                return true;
            }
        }

        return false;
    }
}
