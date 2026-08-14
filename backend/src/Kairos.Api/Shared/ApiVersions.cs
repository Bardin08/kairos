namespace Kairos.Api.Shared;

/// <summary>
/// The route prefix, written once. A breaking change then becomes a second group mapped beside
/// the first — <c>app.MapGroup(ApiVersions.V2)</c> — rather than a find-and-replace across every
/// endpoint file and every client that was already pointed at the old shape.
/// </summary>
public static class ApiVersions
{
    public const string V1 = "/api/v1";
}
