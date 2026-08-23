#!/usr/bin/env bash
# Assert the booking route's first load fits its budgets: gzipped code and raw
# font, measured separately. The numbers live here only — CI and
# scripts/verify.sh both call this script rather than keeping a copy.
#
# Charged: the entry chunk, every chunk it imports eagerly, and their CSS — what
# an invitee downloads for the booking page. CSS counts because a component
# importing a stylesheet makes Vite emit a chunk of it. Lazy chunks, the admin
# workbench the invitee never fetches, are weighed and printed but not charged;
# charging them, which is what globbing assets/ did, let an admin page eat the
# budget that protects the invitee. Eager and lazy come out of Vite's manifest,
# never a hand-kept list: a stale list would fail looking like a regression.
#
# Only JavaScript and CSS are weighed by the bundle number, which is every file
# of code the build emits; fonts get their own number below. An image imported from
# source would land in assets/ and be counted by neither, a hole worth closing
# the first time one appears. The vendored kairos.css is in neither assets/ nor
# any budget — it is a source asset the API serves directly, no build emits it,
# and nothing measures it, so a passing run is not evidence that it fits.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

# 75 KiB gzipped, measured rather than chosen.
#
# It was 60 KiB (61440) until the frontend shell was weighed: React 19.2.8,
# react-dom, wouter and a router with two placeholder routes measured 61641
# bytes gzipped. That figure was calibrated against an earlier React — the same
# shell measures 59779 on 19.1.1 and 59603 on 19.0.0 — so 19.2 added roughly
# 1.9 KiB to a budget with no room left. Pinning React back a minor would have
# bought 1.7 KiB and left the two real pages nothing, so the number moved
# instead. What remains after the framework is the app's share, 13.8 KiB gzipped:
# enough for hand-written UI, not for a date library or a chart library.
readonly BUDGET_GZIP_BYTES=76800

# 32 KiB of font, a SEPARATE number from the code budget on purpose. Fonts are
# weighed raw: WOFF2 carries its own Brotli compression, so gzipping one again
# adds bytes and reports a figure no invitee ever downloads. Folding a raw
# number into a gzipped one would leave both meaningless — a JS regression could
# hide inside font headroom. A regression in either number fails the run.
#
# Charged: only the fonts index.html PRELOADS, what the invitee blocks on. That
# list is read out of the built index.html rather than kept here: a copy would
# drift silently, and the failure would read as a regression rather than a stale
# list. Everything else in fonts/ is weighed and printed as information — the
# latin-ext and cyrillic subsets arrive only for a Polish or Ukrainian name,
# Source Serif is one heading after first paint.
#
# Measured at 31,000 bytes when the type system landed — Source Sans 3 latin at
# 28,740 plus the digits-only IBM Plex Mono subset at 2,260. The 1,768 bytes of
# headroom are for a subset regenerating slightly larger, not for a third face.
readonly BUDGET_FONT_BYTES=32768

readonly OUT_DIR="backend/src/Kairos.Api/wwwroot"
readonly MANIFEST="$OUT_DIR/.vite/manifest.json"

if [ ! -f "$MANIFEST" ]; then
  printf 'bundle-budget: no %s — nothing was built, so nothing was measured\n' "$MANIFEST" >&2
  exit 1
fi

# The old glob could report a pass with no bundle at all: a pattern that matched
# nothing expanded to itself, gzip failed, and `wc -c` still printed 0. A
# manifest that parses but names no entry chunk is the same hazard — an empty
# list weighs nothing, which is under every budget — so the split below fails
# rather than returns empty and the assignment propagates that status. Lazy
# chunks get no such test: a build with no lazy chunk is ordinary, a build with
# no entry never happened.
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

# ---- fonts ------------------------------------------------------------------
# Preloaded fonts come out of the built index.html. A document with no font
# preload at all is a legitimate state — it is what this repo looked like before
# the type system — so an empty list is zero bytes rather than a failure, unlike
# an empty entry-chunk list.
readonly INDEX_HTML="$OUT_DIR/index.html"
if [ ! -f "$INDEX_HTML" ]; then
  printf 'bundle-budget: no %s — the build emits it, so its absence is a fault\n' "$INDEX_HTML" >&2
  exit 1
fi

# shellcheck disable=SC2016  # the $ in the node program is node's, not this shell's
font_partition="$(node -e '
  const [indexPath, outDir] = process.argv.slice(1)
  const fs = require("node:fs")
  const html = fs.readFileSync(indexPath, "utf8")
  const preloaded = new Set()
  // Matches the href of any <link rel="preload" ... as="font">, in either
  // attribute order, which is the only shape this needs to survive.
  for (const tag of html.match(/<link\b[^>]*>/g) ?? []) {
    if (!/rel=["\x27]preload["\x27]/.test(tag)) continue
    if (!/as=["\x27]font["\x27]/.test(tag)) continue
    const href = tag.match(/href=["\x27]([^"\x27]+)["\x27]/)
    if (href) preloaded.add(href[1].replace(/^\//, ""))
  }
  const dir = `${outDir}/fonts`
  const all = fs.existsSync(dir)
    ? fs.readdirSync(dir).filter((f) => f.endsWith(".woff2")).map((f) => `fonts/${f}`)
    : []
  const missing = [...preloaded].filter((f) => !all.includes(f))
  if (missing.length) {
    console.error(`bundle-budget: index.html preloads ${missing.join(", ")}, which the build did not emit`)
    process.exit(1)
  }
  const charged = all.filter((f) => preloaded.has(f))
  const deferred = all.filter((f) => !preloaded.has(f))
  console.log(charged.map((f) => `${outDir}/${f}`).join(" "))
  console.log(deferred.map((f) => `${outDir}/${f}`).join(" "))
' "$INDEX_HTML" "$OUT_DIR")"

deferred_font_line=""
{ read -r charged_font_line; read -r deferred_font_line || true; } <<< "$font_partition"
read -ra charged_fonts <<< "$charged_font_line"
read -ra deferred_fonts <<< "$deferred_font_line"

sum_bytes() {
  local total=0 file
  for file in "$@"; do total=$((total + $(wc -c < "$file"))); done
  printf '%d' "$total"
}

font_size=0
if [ "${#charged_fonts[@]}" -gt 0 ]; then font_size="$(sum_bytes "${charged_fonts[@]}")"; fi
deferred_font_size=0
if [ "${#deferred_fonts[@]}" -gt 0 ]; then deferred_font_size="$(sum_bytes "${deferred_fonts[@]}")"; fi

printf 'bundle-budget: preloaded fonts %d bytes raw across %d file(s), budget %d\n' \
  "$font_size" "${#charged_fonts[@]}" "$BUDGET_FONT_BYTES"
printf 'bundle-budget: deferred fonts %d bytes raw across %d file(s), not charged to the budget\n' \
  "$deferred_font_size" "${#deferred_fonts[@]}"
printf 'bundle-budget: booking route first load %d bytes total (%d gzipped code + %d raw font)\n' \
  "$((booking_size + font_size))" "$booking_size" "$font_size"

over=0
if [ "$booking_size" -ge "$BUDGET_GZIP_BYTES" ]; then
  printf 'bundle-budget: code over budget by %d bytes\n' "$((booking_size - BUDGET_GZIP_BYTES))" >&2
  over=1
fi
if [ "$font_size" -ge "$BUDGET_FONT_BYTES" ]; then
  printf 'bundle-budget: font over budget by %d bytes\n' "$((font_size - BUDGET_FONT_BYTES))" >&2
  over=1
fi
[ "$over" -eq 0 ] || exit 1
