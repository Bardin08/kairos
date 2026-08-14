using Kairos.Api.Scheduling;

using Shouldly;

namespace Kairos.Api.Tests.Scheduling;

public class DurationExtensionsTests
{
    private const int ShortCall = 15;
    private const int StandardCall = 30;
    private const int NeverOffered = 25;

    private static readonly IReadOnlyList<int> TwoDurations = [ShortCall, StandardCall];
    private static readonly IReadOnlyList<int> OneDuration = [StandardCall];
    private static readonly IReadOnlyList<int> NoDurations = [];

    [Fact]
    public void Offers_OfferedDuration_ReturnsTrue() =>
        TwoDurations.Offers(ShortCall).ShouldBeTrue();

    [Fact]
    public void Offers_UnofferedDuration_ReturnsFalse() =>
        TwoDurations.Offers(NeverOffered).ShouldBeFalse();

    /// <remarks>Two distinct durations, so returning the last would not accidentally pass.</remarks>
    [Fact]
    public void Default_MultipleDurations_ReturnsTheFirst() =>
        TwoDurations.Default.ShouldBe(ShortCall);

    [Fact]
    public void IsSingle_OneDuration_ReturnsTrue() =>
        OneDuration.IsSingle.ShouldBeTrue();

    /// <remarks>Nothing on offer is not the same as one thing on offer.</remarks>
    [Fact]
    public void IsSingle_NoDurations_ReturnsFalse() =>
        NoDurations.IsSingle.ShouldBeFalse();
}
