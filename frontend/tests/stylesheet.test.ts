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
