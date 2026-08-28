namespace Kairos.Api.Shared;

/// <summary>
/// Where this deployment answers from outside.
/// </summary>
/// <remarks>
/// Not a mail setting, and never was. The mailer needs this string to write a link into a message
/// and the admin surface needs the same string to hand the operator a link worth copying — one
/// value, so the two cannot disagree about the same deployment. It arrived as
/// <c>Email:PublicBaseUrl</c> because mail was the first consumer; the second one is what made the
/// name wrong.
/// </remarks>
public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>
    /// Null when nothing is configured, which is a complete answer rather than a broken one: the
    /// admin surface then falls back to the browser's own origin. That fallback is right for a
    /// deployment served on one hostname and wrong only for one served on two — which is the case
    /// this setting exists for.
    /// </summary>
    public string? PublicBaseUrl { get; set; }
}
