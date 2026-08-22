using Kairos.Api.Security;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The bookkeeping behind the <c>RateLimit-*</c> headers, at the edges of its cap.
/// </summary>
/// <remarks>
/// Exercised here rather than over HTTP for two reasons. The only condition that frees an entry is
/// the framework disposing an idle partition's limiter, which it does on its own timer some
/// seconds after a partition goes quiet — not something to wait out in a suite, and not something
/// an endpoint can be asked to do on cue. And the sweep is gated on elapsed time, so pinning
/// "at most one per window" needs a clock a test can move rather than one it has to outlast.
/// </remarks>
public class RateLimitBudgetTests
{
    private const int RememberOne = 1;

    private const int Permits = RateLimitOptions.DefaultWritePermits;

    /// <summary>
    /// Opaque on purpose. The type never parses a partition key, so imitating the
    /// <c>policy:address</c> shape the policies happen to build would advertise a coupling that
    /// does not exist.
    /// </summary>
    private const string First = "first";

    private const string Second = "second";
    private const string Third = "third";
    private const string Fourth = "fourth";

    /// <summary>How many windows to age a partition by, to be unambiguously past one.</summary>
    private const int WindowsToAge = 2;

    private static readonly TimeSpan Window = RateLimitOptions.DefaultWindow;

    /// <summary>
    /// The Unix epoch. Where a clock reads zero ticks, which is the reading that made reclamation
    /// silently dead: with no sentinel to tell "never swept" apart from "swept at tick zero", the
    /// gate read an untouched field as a sweep that had just happened and never opened again.
    /// </summary>
    private static readonly Instant Epoch = Instant.FromUnixTimeSeconds(0);

    /// <summary>An ordinary wall-clock reading, for the cases that are about elapsed time.</summary>
    private static readonly Instant Midmorning = Instant.FromUtc(2026, 9, 7, 9, 0, 0);

    /// <summary>
    /// A partition the framework has dropped stops occupying a slot, so the cap bounds how many
    /// callers are remembered at once rather than how many a process may ever serve. Without the
    /// sweep, the first callers to arrive would own the headers until a restart.
    /// </summary>
    /// <remarks>
    /// Run against a clock at the epoch deliberately: this is the first sweep, so it is also what
    /// proves the gate opens on the sentinel rather than on how far the clock sits from 1970.
    /// </remarks>
    [Fact]
    public void Limiter_WhenAFullBudgetHoldsADroppedPartition_ReclaimsItsSlot()
    {
        var budget = NewBudget(new StubClock(Epoch));

        var first = budget.Limiter(First, Permits, Window);
        budget.Read(First).ShouldNotBeNull();

        // Exactly what the framework does to a partition it has decided is idle, and the only
        // signal the sweep acts on.
        first.Dispose();

        using var second = budget.Limiter(Second, Permits, Window);

        // Remembered despite a cap of one, which can only be true if the dropped entry was swept.
        budget.Read(Second).ShouldNotBeNull();
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
        var budget = NewBudget(new StubClock(Midmorning));

        using var first = budget.Limiter(First, Permits, Window);
        using var second = budget.Limiter(Second, Permits, Window);

        budget.Read(First).ShouldNotBeNull();
        budget.Read(Second).ShouldBeNull();
    }

    /// <summary>
    /// The same, for the one shape an eviction rule written on our own bookkeeping would get
    /// wrong: a partition whose budget is untouched and whose window has long since turned over.
    /// That reads as idle by every measure except the only one that counts — the framework still
    /// holds it, so a caller is still being scored against it.
    /// </summary>
    /// <remarks>
    /// This is the case that stayed green through a whole review round. Both tests above run at a
    /// single instant, so an eviction rule reading "full permits and a window elapsed" never fired
    /// in them and could be reinstated unnoticed. Ageing the clock is what makes it fire.
    /// </remarks>
    [Fact]
    public void Limiter_WhenAFullBudgetHoldsAnUntouchedPartitionPastItsWindow_StillKeepsIt()
    {
        var clock = new StubClock(Midmorning);
        var budget = NewBudget(clock);

        using var first = budget.Limiter(First, Permits, Window);

        clock.Now += Duration.FromTimeSpan(Window * WindowsToAge);

        using var second = budget.Limiter(Second, Permits, Window);

        budget.Read(First).ShouldNotBeNull();
        budget.Read(Second).ShouldBeNull();
    }

    /// <summary>
    /// The sweep is rationed to one per window. It walks the whole dictionary, which is what makes
    /// it complete, and that is only affordable because a flood of new partitions — every one of
    /// them arriving over capacity — cannot make it run per request.
    /// </summary>
    [Fact]
    public void Limiter_MeetingASecondNewPartitionInsideOneWindow_DoesNotSweepAgain()
    {
        var clock = new StubClock(Midmorning);
        var budget = NewBudget(clock);

        budget.Limiter(First, Permits, Window).Dispose();

        // Due, because none has run yet: the dropped entry goes and this one takes the slot.
        var second = budget.Limiter(Second, Permits, Window);
        budget.Read(Second).ShouldNotBeNull();

        // Dropped in its turn, so there is something for a second sweep to find if one runs.
        second.Dispose();

        // Not due — the same window — so the slot the entry above is holding stays held, however
        // reclaimable that entry now is.
        using var third = budget.Limiter(Third, Permits, Window);
        budget.Read(Third).ShouldBeNull();

        clock.Now += Duration.FromTimeSpan(Window);

        // Due again, and the wait is the only thing that changed.
        using var fourth = budget.Limiter(Fourth, Permits, Window);
        budget.Read(Fourth).ShouldNotBeNull();
    }

    /// <summary>
    /// A clock stepped backwards still sweeps. NTP correcting a drifting Raspberry Pi leaves the
    /// last-swept stamp in the future, and read as a plain interval that is a large negative
    /// number — indistinguishable from "swept a moment ago", which would hold the gate shut until
    /// real time climbed back past the old stamp. On a minute-long window that is a minute of no
    /// reclamation; on a clock corrected by an hour it is an hour.
    /// </summary>
    [Fact]
    public void Limiter_AfterTheClockStepsBackwards_SweepsRatherThanWaitingForTimeToCatchUp()
    {
        var clock = new StubClock(Midmorning);
        var budget = NewBudget(clock);

        budget.Limiter(First, Permits, Window).Dispose();

        // The first sweep, which stamps the reading this test is about to invalidate.
        var second = budget.Limiter(Second, Permits, Window);
        budget.Read(Second).ShouldNotBeNull();
        second.Dispose();

        clock.Now -= Duration.FromTimeSpan(Window * WindowsToAge);

        using var third = budget.Limiter(Third, Permits, Window);

        budget.Read(Third).ShouldNotBeNull();
    }

    private static RateLimitBudget NewBudget(IClock clock) => new(
        Options.Create(new RateLimitOptions { MaxPartitions = RememberOne }),
        clock,
        NullLogger<RateLimitBudget>.Instance);

    /// <summary>A clock the test moves by hand, so "a window has passed" is a statement, not a wait.</summary>
    private sealed class StubClock(Instant now) : IClock
    {
        public Instant Now { get; set; } = now;

        public Instant GetCurrentInstant() => Now;
    }
}
