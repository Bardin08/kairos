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

// Task 7 moved `data-invalid` from `.field` onto the `.field-row`, and the
// border-color and focus rules below — written for `.field` alone — stopped
// matching with no test failing: an invalid limit still rendered `.field-error`
// while the input beside it kept its ordinary border and focus ring.
test('the invalid-state border and focus ring cover the row shape, not only the field shape', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain(
    `.field[data-invalid="true"] .input,\n  .field[data-invalid="true"] .textarea,\n  ${ROW_INVALID_INPUT} {\n    border-color: var(--status-danger-fg);`,
  )
  expect(css).toContain(
    `.field[data-invalid="true"] .input:focus-visible,\n  .field[data-invalid="true"] .textarea:focus-visible,\n  ${ROW_INVALID_INPUT}:focus-visible {\n    box-shadow: 0 0 0 3px var(--status-danger-bg);`,
  )
})

// `outline: none` on the form controls is what actually removed the ring
// DESIGN.md says is never removed; moving those selectors to :focus-visible was
// bookkeeping beside it. Comments are stripped first: prose naming the
// declaration is not the declaration.
test('no rule in the stylesheet removes the focus outline', () => {
  const css = readFileSync(stylesheet, 'utf8').replace(/\/\*[\s\S]*?\*\//g, '')

  expect(css).not.toContain('outline: none')
})

// A margin or notice in minutes can legitimately run past 999. .ctl-num's width
// and .affix's trailing reservation are derived together (see the comments beside
// each in kairos.css); this pins the two numbers that arithmetic landed on.
test('the numeric control is wide enough for four digits plus the affix reservation', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain('.ctl-num { width: 6.5rem; }')
})

// `a day` (dailyCap's affix in limitCopy.ts) is wider than `min`, the other three
// limits' affix. A single reservation sized for the shorter one is what let the
// spinner-and-affix collision happen; sizing for the wider string serves both.
test('the affix reservation is sized for `a day`, not just `min`', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain('.affix > .input { padding-inline-end: var(--space-8); }')
})

// A design system that puts its own unit text in an input's trailing zone cannot
// also let the browser paint a stepper into that same zone. Scoped to .affix
// rather than a blanket input[type=number] rule, so a plain number field
// elsewhere in the app keeps its native spinner.
test('the native number spinner is suppressed only inside .affix, not for every number input', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain(
    '.affix > input[type="number"] {\n    appearance: textfield;\n    -moz-appearance: textfield;\n  }',
  )
  expect(css).toContain(
    '.affix > input[type="number"]::-webkit-outer-spin-button,\n  .affix > input[type="number"]::-webkit-inner-spin-button {\n    -webkit-appearance: none;\n    margin: 0;\n  }',
  )
  const occurrences = css.split('input[type="number"]').length - 1
  const scopedOccurrences = css.split('.affix > input[type="number"]').length - 1
  expect(occurrences).toBe(scopedOccurrences)
})

// .slots had a cap (300px, scrolling) but no floor, so the desktop slot column —
// and the card around it — was exactly as tall as the chosen day's times: 583px
// on a scrolling 16-time day, ~431px on a 5-time day. Cap and floor are now the
// same --slots-h token, and the space below a short day is deliberately empty.
test('the slot column has a floor equal to its cap, so the card is the same height on every day', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain('min-height: var(--slots-h);')
  expect(css).toContain('max-height: var(--slots-h);')
})

// .segmented's selected style was written for aria-pressed (the duration picker)
// before the date-state radio group needed the same look for aria-checked. One
// rule extended to both selectors rather than a second block copied beside it,
// matched through the closing brace so dropping a declaration fails here too.
test('the segmented selected style covers both toggle and radio semantics in one rule', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain(
    '.segmented button[aria-pressed="true"],\n  .segmented button[aria-checked="true"] {\n    background: var(--surface-raised);\n    color: var(--text-primary);\n    font-weight: var(--weight-semi);\n    box-shadow: var(--shadow-1);\n  }',
  )
})

// Blocked is the only one of the three date states that removes availability, so
// its selection is the one departure from .segmented's neutral treatment
// (--surface-raised + shadow) into the danger palette.
test('a selected segmented option can carry a danger fill instead of the neutral one', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain(
    '.segmented button.segmented-danger[aria-pressed="true"],\n  .segmented button.segmented-danger[aria-checked="true"] {\n    background: var(--status-danger-bg);\n    color: var(--status-danger-fg);\n  }',
  )
})

// A measurement against the running app found .segmented buttons rendering at
// 36px, under DESIGN.md's tap floor (44px minimum, every viewport, no exceptions
// clause). Fixed at the base rule with the --tap token rather than a literal, so
// the duration picker and the date-state control share one height.
test('the segmented button height is the tap-floor token, not a literal', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain('.segmented button {\n    min-height: var(--tap);')
})

// `.admin-main` is the admin surface's only scroll container, and this is the
// declaration that keeps that true. `.visually-hidden` is absolutely positioned,
// so with the pane static a control column's hidden heading resolves against the
// initial containing block instead: it escapes the overflow clip and stretches
// the document to 2073px against a 900px viewport, a second scrollbar nothing in
// the app scrolls back. No rendered test can guard it — jsdom has no layout.
test('the admin pane is a containing block, so a hidden heading cannot escape its scroll clip', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain('.admin-main { position: relative;')
})
