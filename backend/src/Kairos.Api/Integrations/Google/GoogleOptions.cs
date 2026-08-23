namespace Kairos.Api.Integrations.Google;

/// <summary>
/// The client secret is the whole reason any of this touches the backend at all — a browser
/// cannot be trusted to hold it, so the code exchange cannot happen there.
/// </summary>
/// <remarks>
/// Absent configuration is not a startup failure: a Kairos that never connects a calendar is a
/// working Kairos. An unconfigured provider is reported as unconfigured by <c>GET /integrations</c>
/// and refuses at Connect, which is where the operator can do something about it.
/// </remarks>
public sealed class GoogleOptions
{
    /// <summary>In the environment, <c>Integrations__Google__…</c>.</summary>
    public const string SectionName = "Integrations:Google";

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Absolute, and byte-for-byte one of the authorised redirect URIs on the Google Cloud OAuth
    /// client. Google compares strings: a trailing slash, or <c>127.0.0.1</c> where the console
    /// says <c>localhost</c>, is a <c>redirect_uri_mismatch</c> and not a subtle one to debug.
    /// </summary>
    public string RedirectUri { get; set; } = string.Empty;
}
