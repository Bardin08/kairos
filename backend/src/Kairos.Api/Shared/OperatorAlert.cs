using NodaTime;

namespace Kairos.Api.Shared;

/// <summary>
/// One thing the operator has to know about, from anywhere in the application. Provider- and
/// feature-neutral: a stalled outbox would publish to it and the screen would not learn anything.
/// </summary>
/// <param name="Code">Machine-readable, never a sentence: the screen turns codes into copy.</param>
/// <param name="Severity">One of <see cref="AlertSeverity"/>.</param>
/// <param name="Subject">What it is about — a provider, a queue, a recipient — or null.</param>
/// <param name="Since">When the thing last worked, where that is known at all.</param>
/// <param name="Link">
/// A path inside this application, rooted at '/', never absolute: the screen makes it an href, and
/// <see cref="Features.Admin.AlertsAdminEndpoints"/> refuses another origin. Null if nothing to press.
/// </param>
/// <param name="Details">
/// What the copy interpolates and the fixed fields cannot hold. Three rules keep it from becoming a
/// second untyped API:
/// <list type="number">
/// <item>Labels, codes and numbers, never prose, which <paramref name="Code"/> exists to prevent.</item>
/// <item>Never a secret, or anything derived from one. The only reader is a browser.</item>
/// <item>Keys are documented beside the code emitting them; an undocumented key is unread.</item>
/// </list>
/// Empty rather than null, so nothing downstream null-checks it.
/// </param>
public sealed record OperatorAlert(
    string Code,
    string Severity,
    string? Subject,
    Instant? Since,
    string? Link,
    IReadOnlyDictionary<string, string> Details);
