#!/usr/bin/env bash
# The acceptance suite. Run it from anywhere in the repo; it works from the root.
set -uo pipefail
cd "$(git rev-parse --show-toplevel)" || exit 1

# Enough of a failing command's output to act on, without burying the summary.
readonly FAILURE_LOG_LINES=20

LOG_DIR="$(mktemp -d)"
readonly LOG_DIR
trap 'rm -rf "$LOG_DIR"' EXIT

PASS=0; FAIL=0; SKIP=0; CHECKS=0
FAILED=(); FAILED_LOGS=()
LAST_CHECK_OK=yes

check() {  # check <description> <command...>
  local desc="$1"; shift
  local log="$LOG_DIR/$CHECKS.log"
  CHECKS=$((CHECKS+1))
  if "$@" >"$log" 2>&1; then
    LAST_CHECK_OK=yes
    PASS=$((PASS+1)); printf '  ok   %s\n' "$desc"
  else
    LAST_CHECK_OK=no
    FAIL=$((FAIL+1)); FAILED+=("$desc"); FAILED_LOGS+=("$log")
    printf '  FAIL %s\n' "$desc"
  fi
}

skip() {  # skip <description> <reason>
  SKIP=$((SKIP+1)); printf '  skip %s — %s\n' "$1" "$2"
}

# Three states, because they call for three different actions. Reporting any as a
# failure would make the suite red for a reason unrelated to what it checks — the
# same defect class as a check that passes when its subject is missing.
#
#   absent  — Tasks 1-3, no frontend/ at all. CI gates the same way.
#   no-deps — frontend/ exists but nobody ran npm ci in this clone.
#   ready   — run it all.
#
# npm ci is deliberately NOT run from here: this script reports on the tree, it
# does not mutate it, and a sentence naming the command beats a surprise install.
FRONTEND_STATE=absent
if [ -d frontend ]; then
  FRONTEND_STATE=ready
  [ -d frontend/node_modules ] || FRONTEND_STATE=no-deps
fi
readonly FRONTEND_STATE
readonly NO_FRONTEND_YET="frontend/ does not exist yet"
readonly NO_FRONTEND_DEPS="frontend/node_modules is missing — run: (cd frontend && npm ci)"

echo "== supply chain =="
check "no floating nuget versions"   bash -c '! grep -E "Version=\"[^[]" backend/Directory.Packages.props'
check "transitive pinning enabled"   grep -q "CentralPackageTransitivePinningEnabled>true" backend/Directory.Packages.props
# These two read committed files, so they are real work the moment frontend/
# exists — node_modules has nothing to do with them.
if [ "$FRONTEND_STATE" != absent ]; then
  check "no caret ranges in npm"     bash -c '! grep -E "\"[\^~]" frontend/package.json'
  check "npm lockfile committed"     test -f frontend/package-lock.json
else
  skip "no caret ranges in npm"      "$NO_FRONTEND_YET"
  skip "npm lockfile committed"      "$NO_FRONTEND_YET"
fi
check "actions pinned by sha"        bash -c '! grep -rE "uses: .*@v[0-9]" .github/workflows/'
# shellcheck disable=SC2016  # the $() is meant for the inner shell, not this one
check "dependabot split by stack"    bash -c '[ "$(grep -c "package-ecosystem" .github/dependabot.yml)" -ge 3 ]'

echo "== layout =="
check "no razor pages"               bash -c '! find backend -name "*.cshtml" | grep -q .'
# git grep rather than grep -r, so the search sees committed sources and not
# bin/obj: once wwwroot holds files, the static web assets manifests dropped in
# every test project's bin/ name kairos.css, and a plain recursive grep reads
# that as a test reaching for the UI.
check "backend tests ignore the ui"  bash -c '! git grep -lE "\.tsx|kairos\.css|wwwroot" -- backend/tests | grep -q .'
# Asked for by path, not as .git/hooks/…: inside a git worktree .git is a file
# pointing at the real git dir, so the literal path is "not a directory" and the
# check fails on exactly the workflow the plan asks for.
# shellcheck disable=SC2016  # the $() is meant for the inner shell, not this one
check "git hooks installed"          bash -c 'grep -q lefthook "$(git rev-parse --git-path hooks/pre-commit)"'

echo "== backend =="
check "formatted"                    bash -c 'cd backend && dotnet format --verify-no-changes'
check "builds"                       bash -c 'cd backend && dotnet build -warnaserror'
if [ "$LAST_CHECK_OK" = yes ]; then
  check "tests pass"                 bash -c 'cd backend && dotnet test --no-build'
else
  skip "tests pass"                  "the build failed, so --no-build would only test stale binaries"
fi

echo "== frontend =="
if [ "$FRONTEND_STATE" = ready ]; then
  check "lints"                      bash -c 'cd frontend && npx eslint . --max-warnings 0'
  check "typechecks"                 bash -c 'cd frontend && npx tsc --noEmit'
  check "tests pass"                 bash -c 'cd frontend && npx vitest run'
  check "bundle under budget"        bash -c '(cd frontend && npm run build) && bash scripts/bundle-budget.sh'
else
  frontend_reason="$NO_FRONTEND_YET"
  [ "$FRONTEND_STATE" = no-deps ] && frontend_reason="$NO_FRONTEND_DEPS"
  skip "lints"                       "$frontend_reason"
  skip "typechecks"                  "$frontend_reason"
  skip "tests pass"                  "$frontend_reason"
  skip "bundle under budget"         "$frontend_reason"
fi

echo
printf '%d passed, %d skipped, %d failed\n' "$PASS" "$SKIP" "$FAIL"
# Failures are collected rather than only streamed: a long run scrolls, and the
# one failure that matters ends up above the fold. Each carries the tail of its
# own output, so a red line is actionable without re-running the command by hand.
if [ "$FAIL" -gt 0 ]; then
  for i in $(seq 0 $((FAIL - 1))); do
    printf '\n  failed: %s\n' "${FAILED[$i]}"
    tail -n "$FAILURE_LOG_LINES" "${FAILED_LOGS[$i]}" | sed 's/^/    /'
  done
fi
[ "$FAIL" -eq 0 ]
