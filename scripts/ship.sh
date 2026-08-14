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

# A pull request opened seconds ago has no checks attached yet, so a poll that
# gave up immediately would be a race. `timeout(1)` is not on macOS, so the
# deadline is kept here rather than wrapping the wait in an external command.
readonly CHECK_POLL_SECONDS=10
readonly CHECK_TIMEOUT_SECONDS=900

die() { printf 'ship: %s\n' "$1" >&2; exit 1; }

[ "$BRANCH" != "main" ] || die "refusing to ship from main; branch first"

# Untracked files count as dirty. Every task in this plan adds new files, and one
# forgotten `git add` would otherwise ship a pull request missing half the change
# while this script honestly reported success.
if ! git diff --quiet \
  || ! git diff --cached --quiet \
  || [ -n "$(git ls-files --others --exclude-standard)" ]; then
  die "working tree is dirty (tracked edits, staged changes or untracked files); commit or stash first"
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

# Waits for every check to reach a terminal bucket, then fails unless all of them
# are green. Bounded, so a job that never picks up a runner cannot hang the ship
# for the six hours GitHub allows a workflow to run.
wait_for_green_ci() {
  local deadline buckets
  deadline=$(( $(date +%s) + CHECK_TIMEOUT_SECONDS ))

  while :; do
    buckets="$(gh pr checks "$PR" --json bucket --jq '.[].bucket' 2>/dev/null || true)"
    if [ -n "$buckets" ] && ! printf '%s\n' "$buckets" | grep -q '^pending$'; then
      break
    fi
    if [ "$(date +%s)" -ge "$deadline" ]; then
      die "checks on #$PR did not finish within ${CHECK_TIMEOUT_SECONDS}s; not merging"
    fi
    sleep "$CHECK_POLL_SECONDS"
  done

  if printf '%s\n' "$buckets" | grep -qvE '^(pass|skipping)$'; then
    gh pr checks "$PR" || true
    die "CI is not green on #$PR; not merging"
  fi
  printf 'ship: CI green on #%s\n' "$PR"
}

merged() { [ "$(gh pr view "$PR" --json state --jq .state)" = "MERGED" ]; }

wait_for_green_ci

# gh also deletes the local branch and switches the checkout, and it exits
# non-zero if that local step fails even though the remote merge succeeded. The
# state read-back below is the authoritative answer, so this exit code is not.
merge_error="$(gh pr merge "$PR" --squash --delete-branch 2>&1)" || true

if ! merged; then
  # Protection requires the branch to be up to date, so a base that moved while
  # CI ran turns the merge into a 405. Rebase, re-run CI on the new head, retry
  # exactly once.
  printf 'ship: #%s did not merge (%s); rebasing onto origin/main and retrying once\n' "$PR" "$merge_error"
  git fetch origin main
  git rebase origin/main
  git push --force-with-lease
  wait_for_green_ci
  merge_error="$(gh pr merge "$PR" --squash --delete-branch 2>&1)" || true
  merged || die "#$PR still did not merge after a rebase: $merge_error"
fi

# A merge blocked at the last moment must never be reported as a ship.
state="$(gh pr view "$PR" --json state --jq .state)"
merged_at="$(gh pr view "$PR" --json mergedAt --jq .mergedAt)"
if [ "$state" != "MERGED" ] || [ -z "$merged_at" ] || [ "$merged_at" = "null" ]; then
  die "#$PR did not merge (state=$state); nothing shipped"
fi

printf 'ship: #%s merged at %s\n' "$PR" "$merged_at"
