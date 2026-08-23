using System.Globalization;

using Kairos.Api.Persistence;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Kairos.Api.Shared;

/// <summary>
/// SQLITE_BUSY as a queue, not a fault. A write that missed the lock inside
/// <see cref="SqlitePragmas.BusyTimeoutMilliseconds"/> succeeds a moment later, so it answers 503
/// with a Retry-After rather than a 500 the caller can do nothing about.
/// </summary>
/// <remarks>
/// A separate handler ahead of <see cref="DomainExceptionHandler"/>, not a branch inside it or a
/// catch at the call site. Inside, that handler would answer for something that is not a domain
/// concept, and touching nothing but a <see cref="DomainException"/> is what keeps a bug a 500. At
/// the call site it would add a failure branch to three endpoints written to have none.
/// <para>
/// Recognising the code lives in <see cref="SqliteErrors"/>, because
/// <see cref="Persistence.DatabaseHealthCheck"/> recognises the same condition and answers it the
/// other way round — coherent only while both read the same number.
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
