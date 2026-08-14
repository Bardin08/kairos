using System.Text.Json;

using Kairos.Api.Shared;

using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// One SQLite error code becomes a 503; every other one stays a bug. The second half is the
/// half worth guarding — a handler that widened to "any database error is a retry" would turn
/// a broken constraint into advice to try again, and the stack trace would never be logged.
/// </summary>
public class DatabaseBusyExceptionHandlerTests
{
    private const int SqliteBusy = 5;
    private const int SqliteConstraint = 19;
    private const string RetryAfterSeconds = "5";

    [Fact]
    public async Task TryHandleAsync_DatabaseLocked_IsServiceUnavailableWithARetryAfter()
    {
        var context = NewContext();

        var handled = await Handler().TryHandleAsync(
            context, Locked(), TestContext.Current.CancellationToken);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers.RetryAfter.ToString().ShouldBe(RetryAfterSeconds);

        var body = await ReadBodyAsync(context);
        body.GetProperty("status").GetInt32().ShouldBe(StatusCodes.Status503ServiceUnavailable);
        body.GetProperty("code").GetString().ShouldBe(DatabaseBusyExceptionHandler.DatabaseBusyCode);
    }

    /// <summary>
    /// EF hands the failure over wrapped, so a handler that only looked at the exception it was
    /// given would answer 500 for exactly the case this exists for.
    /// </summary>
    [Fact]
    public async Task TryHandleAsync_LockedUnderneathADbUpdateException_IsStillServiceUnavailable()
    {
        var context = NewContext();

        var handled = await Handler().TryHandleAsync(
            context,
            new DbUpdateException("saving failed", Locked()),
            TestContext.Current.CancellationToken);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task TryHandleAsync_AnyOtherSqliteError_IsRefusedAndStaysABug()
    {
        var context = NewContext();

        var handled = await Handler().TryHandleAsync(
            context,
            new SqliteException("constraint failed", SqliteConstraint),
            TestContext.Current.CancellationToken);

        handled.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Body.Length.ShouldBe(0);
    }

    private static SqliteException Locked() => new("database is locked", SqliteBusy);

    private static DatabaseBusyExceptionHandler Handler()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddProblemDetails();

        var provider = services.BuildServiceProvider();
        return new DatabaseBusyExceptionHandler(provider.GetRequiredService<IProblemDetailsService>());
    }

    private static DefaultHttpContext NewContext() => new()
    {
        Response = { Body = new MemoryStream() },
    };

    private static async Task<JsonElement> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return await JsonSerializer.DeserializeAsync<JsonElement>(
            context.Response.Body, cancellationToken: TestContext.Current.CancellationToken);
    }
}
