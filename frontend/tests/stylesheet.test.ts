import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { expect, test } from 'vitest'

// Resolved from the working directory rather than import.meta.url: under the
// jsdom environment Vite rewrites that to an http:// URL, which is not a path.
// Vitest runs with the frontend project root as its working directory.
const stylesheet = resolve(
  process.cwd(),
  '../backend/src/Kairos.Api/wwwroot/kairos.css',
)

// The layer order is the stylesheet's whole override contract: anything the app
// adds later has to know which layer wins. If a build step ever rewrites or
// truncates the vendored file, this is what notices.
test('kairos.css declares its cascade layers in order', () => {
  const css = readFileSync(stylesheet, 'utf8')
  expect(css).toContain(
    '@layer reset, tokens, base, layout, components, states, utilities;',
  )
})

const ROW_INVALID_INPUT = '.field-row[data-invalid="true"] .input'

// Task 7 put the four global limits in `.field-row`s and moved `data-invalid`
// onto the row rather than the `.field` it used to sit on. The border-color and
// focus rules below were written for `.field` alone, and when the row moved
// they stopped matching there without a single test failing: an invalid limit
// still rendered `.field-error`, but the input beside it kept its ordinary
// border and focus ring, because nothing selected `.field-row[data-invalid]`.
// This is what would notice if that selector were ever dropped again.
test('the invalid-state border and focus ring cover the row shape, not only the field shape', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain(
    `.field[data-invalid="true"] .input,\n  .field[data-invalid="true"] .textarea,\n  ${ROW_INVALID_INPUT} {\n    border-color: var(--status-danger-fg);`,
  )
  expect(css).toContain(
    `.field[data-invalid="true"] .input:focus,\n  .field[data-invalid="true"] .textarea:focus,\n  ${ROW_INVALID_INPUT}:focus {\n    box-shadow: 0 0 0 3px var(--status-danger-bg);`,
  )
})

// A margin or notice in minutes can legitimately run past 999. .ctl-num's
// width and .affix's trailing reservation are derived together (see the
// comments beside each in kairos.css) — this pins the two numbers the
// arithmetic landed on, not the reasoning itself, so a future width change
// still has to walk through that math rather than editing the number in place.
test('the numeric control is wide enough for four digits plus the affix reservation', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain('.ctl-num { width: 6.5rem; }')
})

// `a day` (dailyCap's affix in limitCopy.ts) is wider than `min`, the other
// three limits' affix — a single reservation sized for the shorter one is
// exactly what let the spinner-and-affix collision in the bug report happen
// in the first place. Sizing for the wider string serves both.
test('the affix reservation is sized for `a day`, not just `min`', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain('.affix > .input { padding-inline-end: var(--space-8); }')
})

// A design system that puts its own unit text in an input's trailing zone
// cannot also let the browser paint a stepper into that same zone. Scoped to
// .affix rather than a blanket input[type=number] rule, so a plain number
// field elsewhere in the app — should one ever exist — keeps its native
// spinner rather than losing it silently to a rule that reaches further than
// this bug needed it to.
test('the native number spinner is suppressed only inside .affix, not for every number input', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain(
    '.affix > input[type="number"] {\n    appearance: textfield;\n    -moz-appearance: textfield;\n  }',
  )
  expect(css).toContain(
    '.affix > input[type="number"]::-webkit-outer-spin-button,\n  .affix > input[type="number"]::-webkit-inner-spin-button {\n    -webkit-appearance: none;\n    margin: 0;\n  }',
  )
  // Every occurrence of the selector in the sheet is scoped to .affix — there
  // is no second, unscoped input[type="number"] rule reaching further.
  const occurrences = css.split('input[type="number"]').length - 1
  const scopedOccurrences = css.split('.affix > input[type="number"]').length - 1
  expect(occurrences).toBe(scopedOccurrences)
})

// .slots had a cap (300px, scrolling) but no floor, so the desktop slot
// column — and the card around it — was exactly as tall as the chosen day's
// times: 583px on a scrolling 16-time day, ~431px on a 5-time day. The floor
// closes that gap by making the cap and the floor the same --slots-h token,
// so the two can never drift apart into a second version of this bug. The
// space below a short day's times is deliberately empty rather than removed.
test('the slot column has a floor equal to its cap, so the card is the same height on every day', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain('min-height: var(--slots-h);')
  expect(css).toContain('max-height: var(--slots-h);')
})

// .segmented's selected style was written for aria-pressed (the duration
// picker's toggle buttons) before the date-state control needed the same
// look for aria-checked (a radio group). One rule extended to both selectors
// rather than a second block copied beside it — this is what would notice if
// a future edit re-forked them, since only one of the two would keep drifting
// styles in sync with the other. Checked through the closing brace rather than
// the first declaration alone, so dropping `color`, `font-weight` or
// `box-shadow` from the rule would fail this test too, not just go unnoticed.
test('the segmented selected style covers both toggle and radio semantics in one rule', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain(
    '.segmented button[aria-pressed="true"],\n  .segmented button[aria-checked="true"] {\n    background: var(--surface-raised);\n    color: var(--text-primary);\n    font-weight: var(--weight-semi);\n    box-shadow: var(--shadow-1);\n  }',
  )
})

// Blocked is the only one of the three date states that removes availability,
// so its selection is the one departure from .segmented's shipped neutral
// treatment (--surface-raised + shadow) into the danger palette. This is what
// would notice if the danger modifier were ever dropped or pointed back at
// --accent-subtle instead of the status-danger tokens.
test('a selected segmented option can carry a danger fill instead of the neutral one', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain(
    '.segmented button.segmented-danger[aria-pressed="true"],\n  .segmented button.segmented-danger[aria-checked="true"] {\n    background: var(--status-danger-bg);\n    color: var(--status-danger-fg);\n  }',
  )
})

// A measurement against the running app found .segmented buttons rendering
// at 36px — under DESIGN.md's tap floor (44px minimum, every viewport, no
// exceptions clause). Fixed at the base rule with the --tap token rather
// than a literal, so the duration picker and the date-state control share
// one height and neither can quietly slip back under the floor on its own.
test('the segmented button height is the tap-floor token, not a literal', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain('.segmented button {\n    min-height: var(--tap);')
})
