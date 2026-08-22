using Kairos.Api.Security;

using Microsoft.Extensions.Options;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The bookkeeping behind the <c>RateLimit-*</c> headers, at the two edges of its cap.
/// </summary>
/// <remarks>
/// Exercised here rather than over HTTP because the only condition that frees an entry is the
/// framework disposing an idle partition's limiter, which it does on its own timer some seconds
/// after a partition goes quiet — not something to wait out in a suite, and not something an
/// endpoint can be asked to do on cue. Calling <see cref="RateLimitBudget"/> directly is what
/// makes both edges reachable without a sleep.
/// </remarks>
public class RateLimitBudgetTests
{
    private const int RememberOne = 1;

    private const int Permits = RateLimitOptions.DefaultWritePermits;

    private const string FirstPartition = $"{RateLimitPolicies.AnonymousWrite}:203.0.113.9";

    private const string SecondPartition = $"{RateLimitPolicies.AnonymousWrite}:198.51.100.4";

    private static readonly TimeSpan Window = RateLimitOptions.DefaultWindow;

    /// <summary>
    /// A partition the framework has dropped stops occupying a slot, so the cap bounds how many
    /// callers are remembered at once rather than how many a process may ever serve. Without the
    /// sweep, the first callers to arrive would own the headers until a restart.
    /// </summary>
    [Fact]
    public void Limiter_WhenAFullBudgetHoldsADroppedPartition_ReclaimsItsSlot()
    {
        var budget = NewBudget();

        var first = budget.Limiter(FirstPartition, Permits, Window);
        budget.Read(FirstPartition).ShouldNotBeNull();

        // Exactly what the framework does to a partition it has decided is idle, and the only
        // signal the sweep acts on.
        first.Dispose();

        budget.Limiter(SecondPartition, Permits, Window);

        // Remembered despite a cap of one, which can only be true if the dropped entry was swept.
        budget.Read(SecondPartition).ShouldNotBeNull();
    }

    /// <summary>
    /// A partition the framework still holds is never evicted to make room for a newcomer. It
    /// belongs to a caller still being scored, and the factory behind it only runs again on a miss
    /// in the framework's own table — so evicting it here would take that caller's headers away
    /// for the rest of its session with nothing left to restore them.
    /// </summary>
    [Fact]
    public void Limiter_WhenAFullBudgetHoldsOnlyLivePartitions_KeepsThemAndForgetsTheNewcomer()
    {
        var budget = NewBudget();

        budget.Limiter(FirstPartition, Permits, Window);
        budget.Limiter(SecondPartition, Permits, Window);

        budget.Read(FirstPartition).ShouldNotBeNull();
        budget.Read(SecondPartition).ShouldBeNull();
    }

    private static RateLimitBudget NewBudget() => new(
        Options.Create(new RateLimitOptions { MaxPartitions = RememberOne }),
        SystemClock.Instance);
}
