namespace Kairos.Api.Security;

/// <summary>
/// One partition's budget at one instant, as the response headers publish it.
/// </summary>
/// <param name="Limit">What the window allows.</param>
/// <param name="Remaining">What is left of it, from the limiter's own statistics.</param>
/// <param name="Reset">How long until the window starts again.</param>
public readonly record struct RateLimitReading(int Limit, int Remaining, TimeSpan Reset);
