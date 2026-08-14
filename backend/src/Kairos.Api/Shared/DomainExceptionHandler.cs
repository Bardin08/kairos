using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Kairos.Api.Shared;

/// <summary>
/// The one place in the codebase that pairs a domain concept with a status code. Every endpoint
/// is then written as the success path alone: a failure is thrown where it is detected and
/// arrives here as ProblemDetails carrying the machine-readable code the client keys on.
/// </summary>
public sealed class DomainExceptionHandler(IProblemDetailsService problemDetails)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not DomainException domain)
        {
            // Unknown failures are not laundered into a tidy 400. Returning false leaves the
            // pipeline to log the stack trace and answer 500, which is the honest answer for a
            // bug: a friendly status here would hide it from the logs and from the caller alike.
            return false;
        }

        var status = ToStatusCode(domain.Kind);
        context.Response.StatusCode = status;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = domain.Message,
                Extensions = { ["code"] = domain.Code },
            },
        });
    }

    private static int ToStatusCode(DomainErrorKind kind) => kind switch
    {
        DomainErrorKind.NotFound => StatusCodes.Status404NotFound,
        DomainErrorKind.Invalid => StatusCodes.Status400BadRequest,
        DomainErrorKind.Conflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError,
    };
}
