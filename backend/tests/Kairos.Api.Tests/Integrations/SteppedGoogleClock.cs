using GoogleClock = Google.Apis.Util.IClock;

namespace Kairos.Api.Tests.Integrations;

/// <summary>Google's clock, moved only by the test.</summary>
public sealed class SteppedGoogleClock(DateTime startUtc) : GoogleClock
{
    public DateTime UtcNow { get; private set; } = startUtc;

    public DateTime Now => UtcNow.ToLocalTime();

    public void Advance(TimeSpan by) => UtcNow += by;
}
