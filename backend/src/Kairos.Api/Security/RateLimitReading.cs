namespace Kairos.Api.Security;

/// <summary>
/// What one request cost a caller, and what it leaves them. The answer the middleware acts on and
/// the numbers it puts in the headers come off the same count, so a caller cannot be refused by one
/// structure and told about it by another.
/// </summary>
/// <param name="Remaining">What is left of the allowance, never below zero.</param>
/// <param name="Reset">How long until the window starts again.</param>
public readonly record struct RateLimitReading(bool Allowed, int Limit, int Remaining, TimeSpan Reset);
