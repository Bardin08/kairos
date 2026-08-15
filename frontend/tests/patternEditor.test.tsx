import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { Router } from 'wouter'
import { memoryLocation } from 'wouter/memory-location'

// ApiError comes from the real module and is never mocked: the editor reads a
// failure through `asApiError`, which recognises it with `instanceof`. A plain
// object would take the "carries no status" path and the sentence this file
// looks for would be replaced with "could not reach the server".
import { ApiError } from '../src/lib/api'
import PatternEditor from '../src/routes/admin/PatternEditor'
import { listPatterns, type Pattern } from '../src/routes/admin/patterns'

vi.mock('../src/routes/admin/patterns', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/patterns')>()),
  listPatterns: vi.fn(),
}))

// Same file the running app loads. This task adds one layout rule to it, so the
// stylesheet stays the authority on which class names exist.
const stylesheet = resolve(process.cwd(), '../backend/src/Kairos.Api/wwwroot/kairos.css')

const ADMIN_BASE = '/admin'
const LIST = '/admin/availability'
const PATTERN_ID = 'b0a5f0e2-0000-7000-8000-000000000001'
const UNAVAILABLE = 503

const weekdays: Pattern = {
  id: PATTERN_ID,
  name: 'Weekdays',
  rules: [],
  overrides: [],
  usedBy: [],
}

/**
 * Mounted under the base App gives the shell, because the back link's href is
 * one of the things under test: the editor writes `/availability` and wouter
 * prefixes the base. Rendered bare, wouter's default router has no base and the
 * link would point at a path that is not a page.
 */
function renderEditor() {
  const { hook } = memoryLocation({ path: `${LIST}/${PATTERN_ID}` })
  return render(
    <Router hook={hook} base={ADMIN_BASE}>
      <PatternEditor params={{ patternId: PATTERN_ID }} />
    </Router>,
  ).container
}

beforeEach(() => {
  vi.mocked(listPatterns).mockResolvedValue([weekdays])
})

afterEach(cleanup)

/**
 * The pattern names the page, and the way back is a link rather than a button.
 *
 * The editor is a route now, so leaving it is a navigation: a link carries the
 * address, opens in a new tab, and survives the browser's own back button —
 * none of which an onClick handler does.
 */
test('shows the pattern name as the heading and a way back to the list', async () => {
  renderEditor()

  expect(await screen.findByRole('heading', { level: 1, name: weekdays.name })).toBeDefined()
  expect(screen.getByRole('link', { name: /availability/i }).getAttribute('href')).toBe(LIST)
})

/**
 * The server's own sentence, not one made up here.
 *
 * The read is the pattern list — there is no single-pattern endpoint — so the
 * failure that reaches this screen is whatever that list answered with, and it
 * is the only text that says what actually went wrong.
 */
test("reports the server's own words when the pattern will not load", async () => {
  const said = 'The patterns could not be read.'
  vi.mocked(listPatterns).mockRejectedValue(new ApiError(UNAVAILABLE, 'patterns_unavailable', said))

  renderEditor()

  expect((await screen.findByRole('alert')).textContent).toContain(said)
})

/**
 * A typed, stale or deleted id, and none of them is a crash.
 *
 * The address is shareable now, so an id that matches nothing is an ordinary
 * arrival — a bookmark to a pattern deleted in another tab reaches exactly this
 * state, and it is not a failure to report as one.
 */
test('an id that matches no pattern says so and keeps the way back', async () => {
  vi.mocked(listPatterns).mockResolvedValue([])

  renderEditor()

  expect(await screen.findByText(/no longer there/i)).toBeDefined()
  expect(screen.queryByRole('alert')).toBeNull()
  expect(screen.getByRole('link', { name: /availability/i }).getAttribute('href')).toBe(LIST)
})

/**
 * The one class this task adds has to be the one the stylesheet gained. A name
 * that only exists in the markup styles nothing, and it fails silently: the two
 * panes simply stop being two panes, at a width no unit test can see.
 */
test('every class the editor renders exists in kairos.css', async () => {
  const css = readFileSync(stylesheet, 'utf8')
  const container = renderEditor()
  await screen.findByRole('heading', { level: 1, name: weekdays.name })

  const used = new Set(
    [...container.querySelectorAll('[class]')].flatMap((element) => [...element.classList]),
  )

  expect([...used].filter((name) => !css.includes(`.${name}`))).toEqual([])
})
