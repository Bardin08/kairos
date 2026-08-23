using NodaTime;

namespace Kairos.Api.Shared;

/// <summary>
/// One thing the operator has to know about, from any part of the application. Provider-neutral
/// and feature-neutral: integrations publish to it today, a stalled outbox or a bounced
/// confirmation would publish to it tomorrow without the screen learning anything.
/// </summary>
/// <param name="Code">
/// Machine-readable, and never a sentence. The screen turns codes into copy — every other admin
/// surface here already does — and prose from the server is prose no designer ever sees.
/// </param>
/// <param name="Severity">One of <see cref="AlertSeverity"/>.</param>
/// <param name="Subject">What it is about — a provider, a queue, a recipient — or null.</param>
/// <param name="Since">When the thing last worked, where that is known at all.</param>
/// <param name="Link">
/// Where the operator goes to fix it: a path inside this application, rooted at '/', never
/// absolute. The screen turns this into an href, so a source that could name another origin would
/// be a redirect written by a helper — <see cref="Features.Admin.AlertsAdminEndpoints"/> refuses
/// one rather than trusting every future source to behave. Null where there is nothing to press,
/// and a source emitting null says in its own code why.
/// </param>
/// <param name="Details">
/// What the screen's copy interpolates and the fixed fields cannot hold: an account label, a
/// count, a recipient. Three rules bind it, and they are the difference between a metadata bag and
/// a second untyped API:
/// <list type="number">
/// <item>Values are labels, codes and numbers — never prose. A sentence here is the server writing
/// copy again, which <paramref name="Code"/> exists to prevent.</item>
/// <item>Never a secret, and never anything derived from one. The only reader is a browser.</item>
/// <item>Keys are documented beside the code that emits them. An undocumented key is a key no
/// screen will ever read.</item>
/// </list>
/// Empty rather than null when there is nothing to add, so nothing downstream null-checks it.
/// </param>
public sealed record OperatorAlert(
    string Code,
    string Severity,
    string? Subject,
    Instant? Since,
    string? Link,
    IReadOnlyDictionary<string, string> Details);
