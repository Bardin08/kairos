namespace Kairos.Api.Shared;

/// <summary>
/// The ProblemDetails extension every failure in this API carries, and the one member a client
/// branches on. Named here rather than spelled at each writer because the three places that write
/// it are one contract with every client: a typo in any of them is not a compile error, it is a
/// caller that finds no code and falls back to the generic sentence.
/// </summary>
public static class ProblemCode
{
    public const string Key = "code";
}
