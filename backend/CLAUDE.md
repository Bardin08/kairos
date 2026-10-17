# Backend rules

Loaded on top of the repository root `AGENTS.md`, which holds the eight rules that bind every change and is not repeated here. These nine bind C# specifically. Before touching `Scheduling/` or the availability write path, also read `../.agents/invariants.md`.

**8. Weight is correctness — the backend half.** `readonly record struct` on the hot path, justified and *measured*, not assumed. No LINQ in the slot generator's inner loop. `AsNoTracking()` on every read. The frontend half of this rule is the enforced bundle budget, in `frontend/CLAUDE.md`.

**9. Each side knows nothing of the other's internals — the backend half.** No .NET test reads a `.tsx` file, asserts on CSS, or looks in `wwwroot`; frontend rules are enforced by frontend tooling. The only contract is HTTP. The harder form: **`Scheduling/` learns nothing about Google, SMTP or webhooks.** A provider type in a `using` inside the shared kernel means the abstraction leaked.

**11. No test that exists only to have a test.** A test earns its place by failing when something real breaks. Assertions restating the implementation, proving a `&&` commutes, or checking a standard library's own contract are deleted rather than written. What survives tests a decision: half-open intervals, null-versus-zero, max-versus-sum, DST mapping, the write lock, the allocation budget, the foreign-key pragma. Where the subject is I/O against a service we do not control, a test asserting "we called `Events.Insert`" tests a mock; say so and verify by hand instead of pretending.

**13. Google is an implementation, never a shape.** Provider-specific types live under `Integrations/Google/` and nowhere else. Every other layer sees `ICalendarWriter`, `IConferenceCreator`, `IBusySource` and an `IntegrationProvider` value. A second provider costs an adapter and a row, never a schema change or a new table. Checked by reading `CalendarSyncService` and asking whether a second adapter would drop in beside the first — a grep for `Google` was considered and dropped, because it passes a leaky abstraction that never names the provider and fails on harmless mentions.

**14. A provider failure never fails a booking.** Google down, SMTP refusing, a webhook endpoint returning 500 — none may roll back or reject a booking the domain already accepted. Losing a Meet link is recoverable in ten seconds; losing a booking is not recoverable at all.

**15. Every secret that reaches the database is encrypted before it gets there.** This is a finding, not a preference: Litestream 0.5.x cannot encrypt and *silently ignores* an `age:` block rather than refusing the config, so a refresh token written in the clear would have been streamed to object storage in the clear by a tool that accepted an encryption directive and dropped it. Hence `IntegrationCredential.ProtectedPayload` under AES-GCM.

**19. Every route states its access decision at the mapping site.** Anonymous is a decision that gets written down, never a default reached by not typing anything. From the audit that found `GET /api/v1/links` public because nobody had ever had to say otherwise, and the `admin` policy admitting anyone the identity provider would authenticate.

**20. Every anonymous endpoint carries a rate-limit policy**, and one named "none" is an acceptable answer only with the reason beside it. The worked example is the admin group's own "none", argued in `AdminGroup`'s docstring rather than absent from it — everything there is behind a session only the operator's group can hold, and a limit would throttle the operator to defend against the operator.

**21. A policy names its principals.** `RequireAuthenticatedUser()` alone is not an authorization decision; it is the absence of one. It survived a whole phase because it reads as correct on review — the word `admin` in the policy name did the work the code did not.

## Two things the audit of 2026-08-22 taught

Not numbered rules, because they bind a review rather than a plan. They were paid for three times over.

**Mutate the code to test the test.** Three of the four fixes in that audit shipped with a suite that stayed green when the fix was reverted. Each was found by breaking the production code and watching nothing fail. A test that does not die when its subject dies is not coverage, and reading it will not tell you which kind it is.

**A comment that cites a source has to be checked against that source.** Four comments in that audit asserted things the repository itself contradicted — a pod limit that does not exist, a framework behaviour a ten-line probe disproved, a maximum that was off by a factor of two, and a claim about validation that ran in the wrong order. Rule 16 says a comment earns its place; this says it also has to be true.
