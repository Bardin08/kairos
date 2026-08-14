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

# 75 KiB gzipped, and the number is measured rather than chosen.
#
# It was 60 KiB (61440) until the frontend shell was built and weighed, at which
# point the shell alone — React 19.2.8, react-dom, wouter, a router with two
# placeholder routes and nothing else — measured 61641 bytes gzipped. The old
# figure was calibrated against an earlier React: the same shell on React 19.1.1
# measures 59779 and on 19.0.0 measures 59603, so 19.2 added roughly 1.9 KiB and
# pushed a budget that already had no room left over the line.
#
# Pinning React back a minor to fit would have bought 1.7 KiB and left the two
# real pages nothing, so the number moved instead. What is left after the fixed
# cost of the framework is the app's share: 13.8 KiB gzipped for every page,
# component and helper Kairos writes. That is the figure a regression is argued
# against, and it is enough for hand-written UI and not enough for a date
# library or a chart library.
readonly BUDGET_GZIP_BYTES=76800
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
