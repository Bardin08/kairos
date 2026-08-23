# The 21 rules — index

**This file holds no rule text.** It maps each number to the file that holds it, so no rule exists in two places and none can drift. A plan says "all 21 apply" and names only the ones it strains; a number is an interface and is never reused or renumbered.

Rules 8 and 9 each split into two halves that bind different trees. Both halves carry the same number, because they are one obligation seen from two sides.

| # | Rule | Full text lives in |
|---|---|---|
| 1 | Parallelize — waves of independent tasks, one worktree each | `.agents/workflow.md` |
| 2 | Respect each language's conventions | `AGENTS.md` |
| 3 | One public type per file | `AGENTS.md` |
| 4 | The best code is the code that was never written | `AGENTS.md` |
| 5 | Keep it simple | `AGENTS.md` |
| 6 | No direct commits to `main`, every pull request small | `.agents/workflow.md` |
| 7 | Every dependency pinned exactly | `.agents/workflow.md` |
| 8 | Weight is correctness | `backend/CLAUDE.md` and `frontend/CLAUDE.md` |
| 9 | Each side knows nothing of the other's internals | `backend/CLAUDE.md` and `frontend/CLAUDE.md` |
| 10 | `var`, not the type name | `AGENTS.md` |
| 11 | No test that exists only to have a test | `backend/CLAUDE.md` |
| 12 | No magic numbers, not even in test data | `AGENTS.md` |
| 13 | Google is an implementation, never a shape | `backend/CLAUDE.md` |
| 14 | A provider failure never fails a booking | `backend/CLAUDE.md` |
| 15 | Every secret is encrypted before it reaches the database | `backend/CLAUDE.md` |
| 16 | A comment earns its place the way a test does | `AGENTS.md` |
| 17 | No new CSS unless nothing in `kairos.css` expresses the screen | `frontend/CLAUDE.md` |
| 18 | No AI attribution | `AGENTS.md` |
| 19 | Every route states its access decision at the mapping site | `backend/CLAUDE.md` |
| 20 | Every anonymous endpoint carries a rate-limit policy | `backend/CLAUDE.md` |
| 21 | A policy names its principals | `backend/CLAUDE.md` |

## Where they came from

Rules 1–12 and 16 came out of Part A's implementation plan, 13–15 out of Part B's, and 17 and 18 were unnumbered at the foot of the availability editor's plan until 2026-08-16, when the set was consolidated. Rules 19, 20 and 21 came out of the security audit of 2026-08-22 — `backend/CLAUDE.md` also carries the two things that audit taught which bind a review rather than a plan.

**A rule that violates one of these is not done.** They are review rules, not automated ones, except where a tool is named.
