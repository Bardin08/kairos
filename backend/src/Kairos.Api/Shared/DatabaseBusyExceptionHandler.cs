using System.Globalization;

using Kairos.Api.Persistence;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Kairos.Api.Shared;

/// <summary>
/// SQLITE_BUSY as what it actually is: not a fault, a queue. A write that never got the lock
/// inside <see cref="SqlitePragmas.BusyTimeoutMilliseconds"/> has nothing wrong with it — the
/// same request a moment later succeeds — so it answers 503 with a Retry-After rather than a 500
/// that says the server is broken and gives the caller nothing to do about it.
/// </summary>
/// <remarks>
/// A separate handler, registered ahead of <see cref="DomainExceptionHandler"/>, rather than a
/// second branch inside it or a catch at the call site. Inside it, that handler would answer for
/// something that is not a domain concept, and its refusal to touch anything but a
/// <see cref="DomainException"/> is what keeps a bug a 500. At the call site it would mean a
/// failure branch in an endpoint written to have none — three of them, since every write can
/// contend, not just the create.
/// <para>
/// Recognising the code lives in <see cref="SqliteErrors"/> rather than here, because readiness
/// has to recognise the same condition and answer it the other way round —
/// <see cref="Persistence.DatabaseHealthCheck"/> reports a contended database as degraded and
/// keeps the process in service, which is only coherent while both are reading the same number.
/// </para>
/// </remarks>
public sealed class DatabaseBusyExceptionHandler(IProblemDetailsService problemDetails)
    : IExceptionHandler
{
    public const string DatabaseBusyCode = "database_busy";

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
        if (!SqliteErrors.IsDatabaseBusy(exception))
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
}
