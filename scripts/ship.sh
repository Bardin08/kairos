#!/usr/bin/env bash
# Push the current branch, open its pull request if there isn't one, wait for CI,
# squash-merge it, and then prove it actually landed.
#
# Run it from anywhere in the repo: `bash scripts/ship.sh` from the root, or
# `bash ../scripts/ship.sh` from backend/ or frontend/.
set -euo pipefail

# Assign first, then cd. `cd "$(git ...)"` discards the substitution's status —
# only cd's own status is tested — and `cd ""` succeeds, so a git failure there
# would silently leave the script running in the wrong directory.
ROOT="$(git rev-parse --show-toplevel)"
readonly ROOT
cd "$ROOT"

BRANCH="$(git rev-parse --abbrev-ref HEAD)"
readonly BRANCH

# A pull request opened seconds ago has no checks attached yet, so a poll that
# gave up immediately would be a race. `timeout(1)` is not on macOS, so the
# deadline is kept here rather than wrapping the wait in an external command.
readonly CHECK_POLL_SECONDS=10
readonly CHECK_TIMEOUT_SECONDS=900

# GitHub computes mergeability asynchronously, so `mergeable` reads UNKNOWN for a
# short window after every push. Unknown means "ask again shortly", never "fine":
# reading it as fine is how a conflict stays invisible until the deadline.
readonly MERGEABILITY_TIMEOUT_SECONDS=120

# Total wall clock the ship may spend getting the pull request in, across however
# many rebases the base needs. Bounded by time rather than by attempt count: with
# several tasks merging in parallel a rebase is routine, and each attempt costs a
# full CI run, so "two attempts" means nothing while "forty minutes" means what
# it says.
readonly MERGE_DEADLINE_SECONDS=2400

MERGEABILITY=unknown
CI_STATUS=green

die() { printf 'ship: %s\n' "$1" >&2; exit 1; }

# Run git, and make failure fatal, loud and attributed to the step that failed.
#
# `set -e` alone is not enough here. It dies with a bare git message and no idea
# which step produced it, and it is suspended inside `if`/`while`/`&&` conditions
# — which is exactly where a status gets lost, and where a swallowed fetch starts
# looking like a clean rebase.
git_or_die() {  # git_or_die <what-was-being-done> <git args...>
  local what="$1"; shift
  local output status=0
  output="$(git "$@" 2>&1)" || status=$?
  if [ "$status" -ne 0 ]; then
    die "$what failed — 'git $*' exited $status: $(printf '%s' "$output" | tr '\n' ' ')"
  fi
  [ -z "$output" ] || printf '%s\n' "$output"
}

# Fetch origin/main and prove the fetch actually took.
#
# The exit code is not evidence on its own: when a fetch does not update
# origin/main, every command after it succeeds as a no-op — the rebase prints
# "Current branch is up to date", the force-push prints "Everything up-to-date",
# both exit 0. The only honest test is whether the ref the fetch was supposed to
# move now matches the remote.
fetch_origin_main() {  # fetch_origin_main <what-was-being-done>
  local what="$1" remote_tip local_tip
  git_or_die "$what" fetch origin main

  remote_tip="$(git ls-remote origin refs/heads/main)" \
    || die "$what: could not read origin's main tip"
  remote_tip="${remote_tip%%[[:space:]]*}"
  local_tip="$(git rev-parse origin/main)"

  if [ -n "$remote_tip" ] && [ "$remote_tip" != "$local_tip" ]; then
    die "$what: the fetch did not take — origin/main is $local_tip here but $remote_tip on the remote. A refname containing a space breaks every fetch in this repository; look for a macOS duplicate such as 'refs/heads/main 2' under .git/refs and remove it."
  fi
}

[ "$BRANCH" != "main" ] || die "refusing to ship from main; branch first"

# Untracked files count as dirty: one forgotten `git add` would otherwise ship a
# pull request missing half the change while this script reported success.
#
# Computed before the test, not inside it: errexit is suspended in an `if`
# condition, so a `git ls-files` that failed there would yield an empty string
# and wave the dirty tree through.
UNTRACKED="$(git ls-files --others --exclude-standard)"
readonly UNTRACKED
if ! git diff --quiet \
  || ! git diff --cached --quiet \
  || [ -n "$UNTRACKED" ]; then
  die "working tree is dirty (tracked edits, staged changes or untracked files); commit or stash first"
fi

# Pre-flight. A refname containing a space — the macOS duplicate artefact
# "refs/heads/main 2" — makes every fetch in the repository fail while leaving
# rebase and push exiting 0, so it is invisible until it has already cost a CI
# run and it reads like a merge problem rather than a local one. One fetch up
# front is the cheapest possible probe, and it warms origin/main besides.
fetch_origin_main "pre-flight check"

git_or_die "pushing $BRANCH" push --set-upstream origin "$BRANCH"

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

# Sets MERGEABILITY to "ok" or "conflicted", polling through the window where
# GitHub has not computed it yet. Dies if it never settles.
await_mergeability() {
  local deadline answer mergeable state
  deadline=$(( $(date +%s) + MERGEABILITY_TIMEOUT_SECONDS ))

  while :; do
    answer="$(gh pr view "$PR" --json mergeable,mergeStateStatus \
      --jq '[(.mergeable // "UNKNOWN"), (.mergeStateStatus // "UNKNOWN")] | join(" ")' 2>/dev/null || true)"
    mergeable="${answer%% *}"
    state="${answer##* }"

    if [ "$mergeable" = "CONFLICTING" ] || [ "$state" = "DIRTY" ]; then
      MERGEABILITY=conflicted
      return
    fi
    if [ "$mergeable" = "MERGEABLE" ]; then
      MERGEABILITY=ok
      return
    fi

    if [ "$(date +%s)" -ge "$deadline" ]; then
      die "GitHub did not compute mergeability for #$PR within ${MERGEABILITY_TIMEOUT_SECONDS}s (mergeable=${mergeable:-empty}, mergeStateStatus=${state:-empty})"
    fi
    sleep "$CHECK_POLL_SECONDS"
  done
}

# Waits for every check to reach a terminal bucket, then fails unless all of them
# are green. Bounded, so a job that never picks up a runner cannot hang the ship
# for the six hours GitHub allows a workflow to run. Sets CI_STATUS to "green" or
# "conflicted"; dies on red or on the timeout.
wait_for_green_ci() {
  local deadline buckets
  deadline=$(( $(date +%s) + CHECK_TIMEOUT_SECONDS ))
  CI_STATUS=green

  while :; do
    buckets="$(gh pr checks "$PR" --json bucket --jq '.[].bucket' 2>/dev/null || true)"
    if [ -n "$buckets" ] && ! printf '%s\n' "$buckets" | grep -q '^pending$'; then
      break
    fi

    # No checks at all is ambiguous, and the two causes want opposite responses.
    # A conflicted pull request has no buildable merge ref, so GitHub creates no
    # workflow run — not a failed one, none — and waiting the full budget here
    # would blame the deadline for a conflict a rebase fixes in seconds.
    if [ -z "$buckets" ]; then
      await_mergeability
      if [ "$MERGEABILITY" = conflicted ]; then
        CI_STATUS=conflicted
        return
      fi
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
  fetch_origin_main "fetching origin/main before rebasing #$PR"

  # The rebase itself stays a plain conditional: a non-zero here is the expected
  # "needs a human" answer, not an infrastructure failure, and it carries files.
  if ! git rebase origin/main; then
    local conflicting
    conflicting="$(git diff --name-only --diff-filter=U | tr '\n' ' ')"
    git rebase --abort || true
    die "#$PR does not rebase cleanly onto origin/main; conflicting files: ${conflicting:-unknown}; resolve by hand"
  fi

  # Assert the rebase actually took. A rebase onto a stale origin/main exits 0
  # having done nothing, so success is proved by the invariant rather than by
  # the exit code.
  if ! git merge-base --is-ancestor origin/main HEAD; then
    die "rebase of #$PR onto origin/main did not take — origin/main ($(git rev-parse --short origin/main)) is not an ancestor of HEAD ($(git rev-parse --short HEAD)); the working tree may be on a stale base"
  fi

  git_or_die "force-pushing the rebased $BRANCH" push --force-with-lease

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

# An add/add conflict against a base that moved is exactly what a rebase fixes,
# so a conflict goes round the same retry loop rather than exiting. It exits only
# when the rebase itself needs a human, which rebase_onto_main reports by name.
handle_conflict() {
  if [ "$(date +%s)" -ge "$ship_deadline" ]; then
    die "#$PR is conflicted against main; base moved $rebases time(s), still not merged after ${MERGE_DEADLINE_SECONDS}s"
  fi
  rebases=$((rebases + 1))
  printf 'ship: no workflow run will be created — #%s is conflicted against main; rebase %d\n' "$PR" "$rebases"
  rebase_onto_main
}

ship_deadline=$(( $(date +%s) + MERGE_DEADLINE_SECONDS ))
rebases=0
merge_error=""

while :; do
  # Mergeability is settled before the wait, not after it: a conflicted pull
  # request has no workflow run to wait for, so entering the check poll first
  # means waiting out the whole budget for something visible immediately.
  await_mergeability
  if [ "$MERGEABILITY" = conflicted ]; then
    handle_conflict
    continue
  fi

  wait_for_green_ci
  if [ "$CI_STATUS" = conflicted ]; then
    handle_conflict
    continue
  fi

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
    DRAFT)
      die "#$PR cannot be merged (mergeStateStatus=$blocker): $merge_error"
      ;;
    DIRTY)
      handle_conflict
      continue
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
