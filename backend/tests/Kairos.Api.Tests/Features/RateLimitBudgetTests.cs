using Kairos.Api.Security;

using Microsoft.Extensions.Logging.Abstractions;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The count itself, at the edges the HTTP tests reach awkwardly or not at all: the boundary
/// between the last permitted request and the first refused one, the clamp under it, and the roll
/// from one window to the next.
/// </summary>
public class RateLimitBudgetTests
{
    private const int Permits = 3;

    private const int NoneLeft = 0;

    private const int OneSpent = 1;

    /// <summary>How many windows to wait out before the old one is certainly gone.</summary>
    private const int WindowsToOutlast = 3;

    /// <summary>
    /// Opaque on purpose. The type never parses a partition key, so imitating the
    /// <c>class:address</c> shape the middleware happens to build would advertise a coupling that
    /// does not exist.
    /// </summary>
    private const string First = "first";

    private const string Second = "second";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Short enough to outlast in a test. It has to be real time: the entry expires on the cache's
    /// own clock, so a stub that only moved NodaTime forward would roll the count in this
    /// application's arithmetic while the cache went on holding the old entry.
    /// </summary>
    private static readonly TimeSpan BriefWindow = TimeSpan.FromMilliseconds(200);

    [Fact]
    public void Spend_UpToThePermitCount_AllowsEachAndCountsTheRemainderDown()
    {
        var budget = NewBudget();

        for (var spent = 1; spent <= Permits; spent++)
        {
            var reading = budget.Spend(First, Permits, Window);

            reading.Allowed.ShouldBeTrue();
            reading.Limit.ShouldBe(Permits);
            reading.Remaining.ShouldBe(Permits - spent);
        }
    }

    /// <summary>
    /// The request after the last permitted one is refused, and the two are adjacent on purpose:
    /// an off-by-one here is an extra booking mail per window per caller, which is exactly the
    /// thing being rationed.
    /// </summary>
    [Fact]
    public void Spend_PastThePermitCount_RefusesAndHoldsTheRemainderAtZero()
    {
        var budget = NewBudget();

        for (var spent = 0; spent < Permits; spent++)
        {
            budget.Spend(First, Permits, Window).Allowed.ShouldBeTrue();
        }

        var refused = budget.Spend(First, Permits, Window);

        refused.Allowed.ShouldBeFalse();
        refused.Remaining.ShouldBe(NoneLeft);

        // And it stays at zero rather than going negative, however long the caller keeps trying.
        budget.Spend(First, Permits, Window).Remaining.ShouldBe(NoneLeft);
    }

    /// <summary>
    /// The window ends, and the allowance comes back with the next one. Without an expiry on the
    /// entry the first caller to exhaust a budget would be refused for the life of the process.
    /// </summary>
    [Fact]
    public async Task Spend_AfterTheWindowHasPassed_StartsANewOne()
    {
        var budget = NewBudget();

        for (var spent = 0; spent < Permits; spent++)
        {
            budget.Spend(First, Permits, BriefWindow);
        }

        budget.Spend(First, Permits, BriefWindow).Allowed.ShouldBeFalse();

        await Task.Delay(BriefWindow * WindowsToOutlast, TestContext.Current.CancellationToken);

        var afterwards = budget.Spend(First, Permits, BriefWindow);

        afterwards.Allowed.ShouldBeTrue();
        afterwards.Remaining.ShouldBe(Permits - OneSpent);
    }

    /// <summary>
    /// One caller's window is not another's. The partition is the whole of what keeps a flood from
    /// throttling the people it is flooding past.
    /// </summary>
    [Fact]
    public void Spend_OnASecondPartition_CountsItSeparately()
    {
        var budget = NewBudget();

        for (var spent = 0; spent < Permits; spent++)
        {
            budget.Spend(First, Permits, Window);
        }

        budget.Spend(First, Permits, Window).Allowed.ShouldBeFalse();

        var other = budget.Spend(Second, Permits, Window);

        other.Allowed.ShouldBeTrue();
        other.Remaining.ShouldBe(Permits - OneSpent);
    }

    /// <summary>
    /// The reset is what the caller is told to wait, so it has to describe the window it is in —
    /// counting down within it rather than restarting at every request.
    /// </summary>
    [Fact]
    public void Spend_TwiceInOneWindow_ReportsAResetThatIsRunningDown()
    {
        var budget = NewBudget();

        var first = budget.Spend(First, Permits, Window);
        var second = budget.Spend(First, Permits, Window);

        first.Reset.ShouldBeLessThanOrEqualTo(Window);
        second.Reset.ShouldBeGreaterThan(TimeSpan.Zero);
        second.Reset.ShouldBeLessThanOrEqualTo(first.Reset);
    }

    private static RateLimitBudget NewBudget() =>
        new(SystemClock.Instance, NullLogger<RateLimitBudget>.Instance);
}
