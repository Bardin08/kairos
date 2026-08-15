namespace Kairos.Api.Shared;

/// <summary>
/// The authorization policy and the route prefix that stand between an anonymous caller and the
/// operator's workbench.
/// </summary>
public static class AdminGroup
{
    public const string Policy = "admin";

    /// <summary>
    /// Every admin route hangs off this and nothing else does. One place to read when asking
    /// "what is public?", which is the question that gets answered wrongly when the answer is
    /// scattered across twenty MapGet calls.
    /// </summary>
    public static RouteGroupBuilder MapAdmin(this WebApplication app) =>
        app.MapGroup($"{ApiVersions.V1}/admin").RequireAuthorization(Policy);
}
