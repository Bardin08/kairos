using Kairos.Api.Scheduling;

using NodaTime;

using Shouldly;

// NodaTime ships its own Interval; the alias says which one this file means.
using Interval = Kairos.Api.Scheduling.Interval;

namespace Kairos.Api.Tests.Scheduling;

public class IntervalTests
{
    private static Interval Minutes(int start, int end) => new(
        Instant.FromUnixTimeSeconds(start * 60),
        Instant.FromUnixTimeSeconds(end * 60));

    [Fact]
    public void Overlaps_TouchingIntervals_ReturnsFalse() =>
        Minutes(0, 60).Overlaps(Minutes(60, 120)).ShouldBeFalse();

    [Fact]
    public void Overlaps_PartiallyCovering_ReturnsTrue() =>
        Minutes(0, 60).Overlaps(Minutes(59, 120)).ShouldBeTrue();

    [Fact]
    public void Pad_ByFifteenMinutes_ExtendsBothEnds()
    {
        var padded = Minutes(60, 120).Pad(Duration.FromMinutes(15));

        padded.Start.ShouldBe(Instant.FromUnixTimeSeconds(45 * 60));
        padded.End.ShouldBe(Instant.FromUnixTimeSeconds(135 * 60));
    }
}
