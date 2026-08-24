using NodaTime;

namespace Kairos.Api.Tests;

/// <summary>
/// A clock that always answers the same instant.
/// </summary>
/// <remarks>
/// Shared rather than re-declared per suite, which is what it was until three of them wanted it.
/// A test about a boundary — an .ics timestamp, a half-open expiry — cannot name the edge it is
/// testing while the instant moves underneath it.
/// </remarks>
public sealed class FixedClock(Instant now) : IClock
{
    public Instant GetCurrentInstant() => now;
}
