using System.Text.Json;

using Kairos.Api.Shared;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The mapping table, and the refusal that makes it safe. Everything else in the HTTP surface is
/// written as a success path on the strength of these two behaviours.
/// </summary>
public class DomainExceptionHandlerTests
{
    private const string Code = "range_too_wide";

    /// <summary>
    /// The reason no endpoint needs a catch. An unexpected exception must reach the pipeline
    /// untouched — a handler that answered here would turn a bug into a tidy 400 and take the
    /// stack trace out of the log with it.
    /// </summary>
    [Fact]
    public async Task TryHandleAsync_NotADomainException_ReturnsFalseAndLeavesTheResponseAlone()
    {
        var context = NewContext();

        var handled = await Handler().TryHandleAsync(
            context, new InvalidOperationException("a bug"), TestContext.Current.CancellationToken);

        handled.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Body.Length.ShouldBe(0);
    }

    [Theory]
    [InlineData(DomainErrorKind.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(DomainErrorKind.Invalid, StatusCodes.Status400BadRequest)]
    [InlineData(DomainErrorKind.Conflict, StatusCodes.Status409Conflict)]
    public async Task TryHandleAsync_DomainException_WritesTheKindsStatusAndCarriesTheCode(
        DomainErrorKind kind,
        int expectedStatus)
    {
        var context = NewContext();

        var handled = await Handler().TryHandleAsync(
            context, new DomainException(kind, Code, "no"), TestContext.Current.CancellationToken);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(expectedStatus);

        var body = await ReadBodyAsync(context);
        body.GetProperty("status").GetInt32().ShouldBe(expectedStatus);
        body.GetProperty("code").GetString().ShouldBe(Code);
    }

    private static DomainExceptionHandler Handler()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddProblemDetails();

        var provider = services.BuildServiceProvider();
        return new DomainExceptionHandler(provider.GetRequiredService<IProblemDetailsService>());
    }

    /// <summary>
    /// DefaultHttpContext hands out Stream.Null, which accepts writes and forgets them. A real
    /// buffer is what lets the body be read back rather than assumed.
    /// </summary>
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
