#!/usr/bin/env bash
# Assert the booking route's first load fits its gzipped budget.
#
# The budget lives here and nowhere else — CI and scripts/verify.sh both call
# this script rather than carrying their own copy of the number.
#
# Scope: what an invitee actually downloads to see the booking page — the entry
# chunk, every chunk it imports eagerly, and the CSS those chunks pull into the
# initial document. CSS counts because the moment a component imports a
# stylesheet Vite emits a chunk of it, and an unweighed output is a budget with
# a hole in it.
#
# Lazily-loaded chunks — the admin workbench, code-split precisely so the
# invitee never fetches it — are weighed and printed as information, and are
# deliberately not charged. Charging them, which is what globbing assets/ did,
# made the number answer the wrong question: adding an admin page the booking
# route never loads ate the budget that exists to protect the invitee.
#
# Eager and lazy come out of Vite's manifest rather than a hand-kept list of
# filenames. The manifest is written by the build that emits the chunks, so it
# cannot drift; a list here would, silently, and the failure would look like a
# budget regression rather than a stale list.
#
# Only JavaScript and CSS are weighed, which is every file the build emits
# today. An image or font imported from source would be emitted into assets/ and
# counted by neither number — the same hole the old glob had, and worth closing
# the first time one appears.
#
# The vendored kairos.css is NOT counted and is not in assets/ — it is a source
# asset the API serves directly, it is not emitted by any build, and nothing
# measures it. Do not read a passing run as evidence that it is within a budget.
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
readonly OUT_DIR="backend/src/Kairos.Api/wwwroot"
readonly MANIFEST="$OUT_DIR/.vite/manifest.json"

if [ ! -f "$MANIFEST" ]; then
  printf 'bundle-budget: no %s — nothing was built, so nothing was measured\n' "$MANIFEST" >&2
  exit 1
fi

# The old glob had a way of reporting a pass with no bundle at all: a pattern
# that matched nothing expanded to itself, gzip failed, and `wc -c` still
# printed 0. The manifest has the same shape of hazard — a file that parses but
# names no entry chunk yields an empty file list, and an empty list weighs
# nothing, which is under every budget. So the split below fails rather than
# returns empty, and the assignment propagates that exit status. Lazy chunks get
# no such test: a build with no lazy chunk, like a build with no CSS chunk, is
# ordinary, while a build with no entry never happened.
# shellcheck disable=SC2016  # the $ and ` in the node program are node's, not this shell's
partition="$(node -e '
  const [manifestPath, outDir] = process.argv.slice(1)
  const manifest = JSON.parse(require("node:fs").readFileSync(manifestPath, "utf8"))
  const eager = new Set()
  const walk = (key) => {
    if (!manifest[key] || eager.has(key)) return
    eager.add(key)
    // `imports` is the static import graph and `dynamicImports` is not, which
    // is the whole distinction this script exists to make.
    for (const next of manifest[key].imports ?? []) walk(next)
  }
  for (const [key, chunk] of Object.entries(manifest)) if (chunk.isEntry) walk(key)
  if (eager.size === 0) {
    console.error(`bundle-budget: ${manifestPath} names no entry chunk — nothing was built, so nothing was measured`)
    process.exit(1)
  }
  const filesOf = (keys) =>
    keys
      .flatMap((key) => [manifest[key].file, ...(manifest[key].css ?? [])])
      .filter((file) => /\.(js|css)$/.test(file))
      .map((file) => `${outDir}/${file}`)
  const lazy = Object.keys(manifest).filter((key) => !eager.has(key))
  console.log(filesOf([...eager]).join(" "))
  console.log(filesOf(lazy).join(" "))
' "$MANIFEST" "$OUT_DIR")"

# Two lines out, eager then lazy. The second read is allowed to fail: command
# substitution strips trailing newlines, so a build with no lazy chunk — as
# ordinary as a build with no CSS chunk — leaves the second line with nothing on
# it and `read` reports EOF, which errexit would otherwise take for a fault.
lazy_line=""
{ read -r booking_line; read -r lazy_line || true; } <<< "$partition"
read -ra booking_files <<< "$booking_line"
read -ra lazy_files <<< "$lazy_line"

booking_size="$(gzip -c "${booking_files[@]}" | wc -c)"
booking_size=$((booking_size))

lazy_size=0
if [ "${#lazy_files[@]}" -gt 0 ]; then
  lazy_size="$(gzip -c "${lazy_files[@]}" | wc -c)"
  lazy_size=$((lazy_size))
fi

printf 'bundle-budget: booking route %d bytes gzipped across %d file(s), budget %d\n' \
  "$booking_size" "${#booking_files[@]}" "$BUDGET_GZIP_BYTES"
printf 'bundle-budget: lazy chunks %d bytes gzipped across %d file(s), not charged to the budget\n' \
  "$lazy_size" "${#lazy_files[@]}"

if [ "$booking_size" -ge "$BUDGET_GZIP_BYTES" ]; then
  printf 'bundle-budget: over budget by %d bytes\n' "$((booking_size - BUDGET_GZIP_BYTES))" >&2
  exit 1
fi
