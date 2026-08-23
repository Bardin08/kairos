using Kairos.Api.Persistence.Entities;
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
    /// <summary>
    /// Deliberately not the endpoint's code and not its sentence. This helper lives in Shared/
    /// and knows nothing about patterns — a test reaching for <c>PatternChangedCode</c> would
    /// read as though it did, and would go on passing by copying whatever the endpoint copied.
    /// Any two strings prove the same thing, so these say plainly that they are the caller's.
    /// </summary>
    private const string Code = "some_caller_code";
    private const string Message = "Whatever the caller wanted to say about it.";
    private const int Answer = 42;

    /// <summary>One name, two patterns — the unique index on Patterns.Name is what refuses it.</summary>
    private const string TakenName = "Weekdays";

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
    /// The helper's other shape, against a violation SQLite actually raised rather than one built
    /// by hand. A duplicate arrives as an extended error code on an exception EF has wrapped, so
    /// a constructed exception would pin the constructor and not the shape — and this is now the
    /// only thing exercising the branch at all, because the token on the pattern means the
    /// endpoint reaches the concurrency exception first and never gets here.
    /// </summary>
    [Fact]
    public async Task NamedAsync_WhenTheRowWasAlreadyInserted_IsAConflictNamingTheCode()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        context.Patterns.Add(new Pattern { Id = Guid.CreateVersion7(), Name = TakenName });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Patterns.Add(new Pattern { Id = Guid.CreateVersion7(), Name = TakenName });

        var failure = await Should.ThrowAsync<DomainException>(() => ConcurrentWrite.NamedAsync(
            () => context.SaveChangesAsync(TestContext.Current.CancellationToken),
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
