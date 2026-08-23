# Workflow

Read before starting a task, before opening a pull request, and before editing a dependency. Holds rules 1, 6 and 7 in full.

**1. Parallelize.** Tasks group into waves. Every task in a wave is independent — disjoint files, no ordering constraint — and must be dispatched concurrently, each in its own worktree, then merged. A task that cannot be parallelized names the file it collides on.

**6. No direct commits to `main`, and every pull request small.** One task, one pull request, CI green. A pull request big enough that reviewing it properly takes an afternoon gets approved without being read, and cannot be reverted without taking unrelated work with it. A diff growing past a few hundred lines means the task should have been two.

**7. Every dependency pinned exactly.** NuGet bracket notation `[1.2.3]` under central package management with transitive pinning; npm exact strings, no `^`, lockfile committed, installed with `npm ci`; GitHub Actions by commit SHA with the tag in a trailing comment. Updates arrive as Dependabot pull requests **separated by ecosystem** — backend NuGet, frontend npm, Actions — never auto-merged. Supply-chain compromise is the threat being defended against.

## The commands

| Command | From | Does |
|---|---|---|
| `scripts/verify.sh` | repo root | The gate before a push |
| `scripts/ship.sh` | repo root | Opens the pull request; refuses to claim success for one that did not land |
| `scripts/bundle-budget.sh` | repo root | The booking route's enforced weight budget |
| `dotnet format --verify-no-changes` | `backend/` | Enforces rule 10 and the rest of `.editorconfig` |
| `npx eslint --max-warnings 0` | `frontend/` | Runs on staged files at pre-commit |

`lefthook.yml` runs formatting at pre-commit and the backend suite at pre-push, deliberately: running the full suite on every commit teaches people to pass `--no-verify`, which is worse than having no hook at all.

Getting the app running locally is `docs/running-locally.md`.

## Branch protection

`main` is protected against everyone, admins included. There is no path that bypasses a pull request, and none is to be added.

## Naming

**Commits are [Conventional Commits](https://www.conventionalcommits.org/).** `<type>(<scope>)?: <subject>`. The types in use are `feat`, `fix`, `docs`, `chore`, `test` and `refactor`. Scope is optional and names the tree or the feature area where it earns its place — `chore(frontend)`, `chore(deps)`, `fix(pattern-editor)`. The subject is lower case, describes what the change does rather than what was touched, and takes no trailing full stop.

**A pull request title is a commit message, because the merge is a squash.** `ship.sh` squash-merges, so the title lands on `main` verbatim with `(#N)` appended and nothing gets a second chance to fix it. Nineteen of the last hundred subjects on `main` are prose rather than a conventional subject, every one of them recent and every one of them a pull request title. Write the title as `<type>: <subject>` and the trunk stays parseable.

**Branches are `<type>/<slug>`**, the type matching the commit type: `feat/secure-cookie`, `fix/pattern-editor`, `docs/agentic-setup`. Dependabot names its own and is left alone.

Rule 18 binds all three: no AI attribution in a commit message, a pull request title or body, or a trailer.
