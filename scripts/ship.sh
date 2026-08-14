#!/usr/bin/env bash
# Push the current branch, open its pull request if there isn't one, wait for CI,
# squash-merge it, and then prove it actually landed.
#
# Run it from anywhere in the repo: `bash scripts/ship.sh` from the root, or
# `bash ../scripts/ship.sh` from backend/ or frontend/.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

BRANCH="$(git rev-parse --abbrev-ref HEAD)"
readonly BRANCH

die() { printf 'ship: %s\n' "$1" >&2; exit 1; }

[ "$BRANCH" != "main" ] || die "refusing to ship from main; branch first"
if ! git diff --quiet || ! git diff --cached --quiet; then
  die "working tree is dirty; commit or stash first"
fi

git push --set-upstream origin "$BRANCH"

if ! gh pr view --json number >/dev/null 2>&1; then
  gh pr create --fill
fi

# Capture the number now: --delete-branch removes the branch that `gh pr view`
# would otherwise resolve against, and a number stays valid after the merge.
PR="$(gh pr view --json number --jq .number)"
readonly PR
printf 'ship: pull request #%s for %s\n' "$PR" "$BRANCH"

# --watch blocks until every check reports, and exits non-zero if any failed.
gh pr checks "$PR" --watch || die "CI is red on #$PR; not merging"

gh pr merge "$PR" --squash --delete-branch

# gh can return success while the merge is still settling, and a merge blocked at
# the last moment must never be reported as a ship.
state="$(gh pr view "$PR" --json state --jq .state)"
merged_at="$(gh pr view "$PR" --json mergedAt --jq .mergedAt)"
[ "$state" = "MERGED" ] && [ -n "$merged_at" ] && [ "$merged_at" != "null" ] \
  || die "#$PR did not merge (state=$state); nothing shipped"

printf 'ship: #%s merged at %s\n' "$PR" "$merged_at"
