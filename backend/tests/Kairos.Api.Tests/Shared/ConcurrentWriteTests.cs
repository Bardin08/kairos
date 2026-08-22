using Kairos.Api.Shared;

using Microsoft.EntityFrameworkCore;

using Shouldly;

namespace Kairos.Api.Tests.Shared;

/// <summary>
/// The mapping alone, driven directly. The interleave that produces the exception in production —
/// two requests loading a date's rows before either saves — cannot be forced through an
/// <see cref="HttpClient"/>, so what is pinned here is the translation, and the interleave itself
/// is pinned against EF in <c>SchemaTests</c>.
/// </summary>
public class ConcurrentWriteTests
{
    private const string Code = "override_date_taken";
    private const string Message = "This date was changed somewhere else. Reload and try again.";
    private const int Answer = 42;

    [Fact]
    public async Task NamedAsync_WhenTheWriteSucceeds_ReturnsItsValue()
    {
        var result = await ConcurrentWrite.NamedAsync(() => Task.FromResult(Answer), Code, Message);

        result.ShouldBe(Answer);
    }

    [Fact]
    public async Task NamedAsync_WhenTheRowWasChangedUnderIt_IsAConflictNamingTheCode()
    {
        var failure = await Should.ThrowAsync<DomainException>(() => ConcurrentWrite.NamedAsync<int>(
            () => throw new DbUpdateConcurrencyException(),
            Code,
            Message));

        failure.Kind.ShouldBe(DomainErrorKind.Conflict);
        failure.Code.ShouldBe(Code);
    }

    /// <summary>
    /// An exception this helper has no opinion about travels untouched. A catch-all here would
    /// answer 409 to a disk that filled up, which is the failure mode a wrapper like this has.
    /// </summary>
    [Fact]
    public async Task NamedAsync_OnAnUnrelatedFailure_LetsItThrough()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => ConcurrentWrite.NamedAsync<int>(
            () => throw new InvalidOperationException(),
            Code,
            Message));
    }
}
