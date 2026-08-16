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
