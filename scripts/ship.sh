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

# Total wall clock the ship may spend getting the pull request in, across however
# many rebases the base needs. Bounded by time rather than by attempt count: with
# several tasks merging in parallel a rebase is routine, and each attempt costs a
# full CI run, so "two attempts" means nothing while "forty minutes" means what
# it says.
readonly MERGE_DEADLINE_SECONDS=2400

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

failing_checks() {
  gh pr checks "$PR" --json name,bucket \
    --jq '[.[] | select(.bucket != "pass" and .bucket != "skipping") | "\(.name)=\(.bucket)"] | join(", ")'
}

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

  # A red check is never retryable. Say which one and stop, rather than rebasing
  # into the deadline and reporting a timeout for what is really a broken build.
  if printf '%s\n' "$buckets" | grep -qvE '^(pass|skipping)$'; then
    die "CI is red on #$PR ($(failing_checks)); not merging"
  fi
  printf 'ship: CI green on #%s\n' "$PR"
}

merged() { [ "$(gh pr view "$PR" --json state --jq .state)" = "MERGED" ]; }

# Why the merge did not go through, taken from GitHub's own view of the pull
# request rather than by pattern-matching gh's error prose.
merge_blocker() {
  local buckets
  buckets="$(gh pr checks "$PR" --json bucket --jq '.[].bucket' 2>/dev/null || true)"
  if printf '%s\n' "$buckets" | grep -qE '^(fail|cancel)$'; then
    echo "red-ci"
    return
  fi
  gh pr view "$PR" --json mergeStateStatus --jq .mergeStateStatus 2>/dev/null || echo "UNKNOWN"
}

rebase_onto_main() {
  git fetch origin main
  if ! git rebase origin/main; then
    git rebase --abort || true
    die "#$PR does not rebase cleanly onto origin/main; resolve the conflict by hand"
  fi
  git push --force-with-lease

  # GitHub takes a moment to attribute the pull request to the new head. Until it
  # does, `gh pr checks` still answers for the previous head, and the next merge
  # would be attempted against a green that no longer describes this code.
  local want deadline
  want="$(git rev-parse HEAD)"
  deadline=$(( $(date +%s) + CHECK_TIMEOUT_SECONDS ))
  while [ "$(gh pr view "$PR" --json headRefOid --jq .headRefOid 2>/dev/null || true)" != "$want" ]; do
    if [ "$(date +%s)" -ge "$deadline" ]; then
      die "#$PR head did not catch up with $want within ${CHECK_TIMEOUT_SECONDS}s"
    fi
    sleep "$CHECK_POLL_SECONDS"
  done
}

ship_deadline=$(( $(date +%s) + MERGE_DEADLINE_SECONDS ))
rebases=0
merge_error=""

while :; do
  wait_for_green_ci

  # gh also deletes the local branch and switches the checkout, and it exits
  # non-zero if that local step fails even though the remote merge succeeded. The
  # state read-back is the authoritative answer, so this exit code is not.
  merge_error="$(gh pr merge "$PR" --squash --delete-branch 2>&1)" || true
  merged && break

  blocker="$(merge_blocker)"
  case "$blocker" in
    red-ci)
      die "CI went red on #$PR ($(failing_checks)) before the merge; not retrying"
      ;;
    DIRTY|DRAFT)
      die "#$PR cannot be merged (mergeStateStatus=$blocker): $merge_error"
      ;;
  esac

  if [ "$(date +%s)" -ge "$ship_deadline" ]; then
    die "#$PR: base moved $rebases time(s), still not merged after ${MERGE_DEADLINE_SECONDS}s (mergeStateStatus=$blocker); last merge error: $merge_error"
  fi

  if [ "$blocker" = "BEHIND" ]; then
    rebases=$((rebases + 1))
    printf 'ship: base moved under #%s; rebase %d onto origin/main\n' "$PR" "$rebases"
    rebase_onto_main
  else
    printf 'ship: #%s not mergeable yet (mergeStateStatus=%s); waiting\n' "$PR" "$blocker"
    sleep "$CHECK_POLL_SECONDS"
  fi
done

# A merge blocked at the last moment must never be reported as a ship.
state="$(gh pr view "$PR" --json state --jq .state)"
merged_at="$(gh pr view "$PR" --json mergedAt --jq .mergedAt)"
if [ "$state" != "MERGED" ] || [ -z "$merged_at" ] || [ "$merged_at" = "null" ]; then
  die "#$PR did not merge (state=$state); nothing shipped"
fi

printf 'ship: #%s merged at %s after %d rebase(s)\n' "$PR" "$merged_at" "$rebases"
