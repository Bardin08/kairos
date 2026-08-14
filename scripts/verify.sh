#!/usr/bin/env bash
# The acceptance suite. Run it from anywhere in the repo; it works from the root.
set -uo pipefail
cd "$(git rev-parse --show-toplevel)" || exit 1

# 60 KiB gzipped, the same budget the frontend CI job enforces.
readonly BUNDLE_BUDGET_GZIP_BYTES=61440

PASS=0; FAIL=0; FAILED=()

check() {  # check <description> <command...>
  local desc="$1"; shift
  if "$@" >/dev/null 2>&1; then
    PASS=$((PASS+1)); printf '  ok   %s\n' "$desc"
  else
    FAIL=$((FAIL+1)); FAILED+=("$desc"); printf '  FAIL %s\n' "$desc"
  fi
}

echo "== supply chain =="
check "no floating nuget versions"   bash -c '! grep -E "Version=\"[^[]" backend/Directory.Packages.props'
check "transitive pinning enabled"   grep -q "CentralPackageTransitivePinningEnabled>true" backend/Directory.Packages.props
check "no caret ranges in npm"       bash -c '! grep -E "\"[\^~]" frontend/package.json'
check "npm lockfile committed"       test -f frontend/package-lock.json
check "actions pinned by sha"        bash -c '! grep -E "uses: .*@v[0-9]" .github/workflows/ci.yml'
check "dependabot split by stack"    bash -c 'grep -c "package-ecosystem" .github/dependabot.yml | grep -qx 3'

echo "== layout =="
check "no razor pages"               bash -c '! find backend -name "*.cshtml" | grep -q .'
check "backend tests ignore the ui"  bash -c '! grep -rlE "\.tsx|kairos\.css|wwwroot" backend/tests | grep -q .'

echo "== backend =="
check "formatted"                    bash -c 'cd backend && dotnet format --verify-no-changes'
check "builds"                       bash -c 'cd backend && dotnet build -warnaserror'
check "tests pass"                   bash -c 'cd backend && dotnet test --no-build'

echo "== frontend =="
check "lints"                        bash -c 'cd frontend && npx eslint . --max-warnings 0'
check "typechecks"                   bash -c 'cd frontend && npx tsc --noEmit'
check "tests pass"                   bash -c 'cd frontend && npx vitest run'
check "bundle under budget"          bash -c "cd frontend && npm run build && [ \"\$(gzip -c ../backend/src/Kairos.Api/wwwroot/assets/*.js | wc -c)\" -lt ${BUNDLE_BUDGET_GZIP_BYTES} ]"

echo
printf '%d passed, %d failed\n' "$PASS" "$FAIL"
# Descriptions are collected, not just streamed: a long run scrolls and the one
# failure that matters ends up above the fold.
for d in "${FAILED[@]:-}"; do [ -n "$d" ] && printf '  failed: %s\n' "$d"; done
[ "$FAIL" -eq 0 ]
