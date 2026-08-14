#!/usr/bin/env bash
# Assert the built SPA's JavaScript fits its gzipped budget.
#
# The budget lives here and nowhere else — CI and scripts/verify.sh both call
# this script rather than carrying their own copy of the number.
#
# Scope: the JavaScript bundle only. kairos.css is deliberately NOT counted; the
# plan states the budget against JS. Do not read a passing run as evidence that
# the stylesheet is within any budget, because nothing measures it.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

# 60 KiB gzipped.
readonly BUDGET_GZIP_BYTES=61440
readonly ASSET_DIR="backend/src/Kairos.Api/wwwroot/assets"

# A glob that matches nothing expands to the literal pattern, gzip then fails,
# and `wc -c` still prints 0 — which used to sail under the budget and report a
# pass with no bundle at all. nullglob plus an explicit emptiness test is what
# makes "nothing was built" a failure instead of a green tick.
shopt -s nullglob
files=("$ASSET_DIR"/*.js)
shopt -u nullglob

if [ "${#files[@]}" -eq 0 ]; then
  printf 'bundle-budget: no JavaScript in %s — nothing was built, so nothing was measured\n' "$ASSET_DIR" >&2
  exit 1
fi

size="$(gzip -c "${files[@]}" | wc -c)"
size=$((size))

printf 'bundle-budget: %d bytes gzipped across %d file(s), budget %d\n' \
  "$size" "${#files[@]}" "$BUDGET_GZIP_BYTES"

if [ "$size" -ge "$BUDGET_GZIP_BYTES" ]; then
  printf 'bundle-budget: over budget by %d bytes\n' "$((size - BUDGET_GZIP_BYTES))" >&2
  exit 1
fi
