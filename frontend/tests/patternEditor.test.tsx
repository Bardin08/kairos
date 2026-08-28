import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { Router } from 'wouter'
import { memoryLocation } from 'wouter/memory-location'

// ApiError comes from the real module and is never mocked: the editor reads a
// failure through `asApiError`, which recognises it with `instanceof`. A plain
// object would take the "carries no status" path and the sentence this file
// looks for would be replaced with "could not reach the server".
import { ApiError } from '../src/lib/api'
import PatternEditor from '../src/routes/admin/PatternEditor'
import {
  listPatterns,
  renamePattern,
  replacePatternRules,
  type Pattern,
} from '../src/routes/admin/patterns'

vi.mock('../src/routes/admin/patterns', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/patterns')>()),
  listPatterns: vi.fn(),
  renamePattern: vi.fn(),
  replacePatternRules: vi.fn(),
}))

// Same file the running app loads. Whatever this page adds to it, the
// stylesheet stays the authority on which class names exist.
const stylesheet = resolve(process.cwd(), '../backend/src/Kairos.Api/wwwroot/kairos.css')

const ADMIN_BASE = '/admin'
const LIST = '/admin/availability'
const PATTERN_ID = 'b0a5f0e2-0000-7000-8000-000000000001'
const UNAVAILABLE = 503
const CONFLICT = 409

// Minutes from local midnight, which is what a rule holds.
const MINUTES_PER_HOUR = 60
const NINE_AM = 9 * MINUTES_PER_HOUR
const TEN_AM = 10 * MINUTES_PER_HOUR
const NOON = 12 * MINUTES_PER_HOUR
const ONE_PM = 13 * MINUTES_PER_HOUR
const FOUR_PM = 16 * MINUTES_PER_HOUR
const FIVE_PM = 17 * MINUTES_PER_HOUR

// ISO-8601: 1 is Monday.
const MONDAY = 1
const TUESDAY = 2
const WEDNESDAY = 3

/** Outside 1-7, which the column permits and the week has no square for. */
const DAY_THAT_IS_NOT_A_DAY = 9

const weekdays: Pattern = {
  id: PATTERN_ID,
  name: 'Weekdays',
  rules: [{ id: 'r1', isoDayOfWeek: MONDAY, startMinute: NINE_AM, endMinute: FIVE_PM }],
  overrides: [],
  usedBy: [],
}

/** The one pattern the list answers with, so a test states only the week it is about. */
function patternWith(rules: Pattern['rules']): void {
  vi.mocked(listPatterns).mockResolvedValue([{ ...weekdays, rules }])
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
  // Call counts, not implementations: several of these tests assert that the
  // whole week went in exactly one request, and a count that carried over from
  // the previous test would pass or fail for the wrong reason.
  vi.clearAllMocks()
  vi.mocked(listPatterns).mockResolvedValue([weekdays])
  vi.mocked(replacePatternRules).mockResolvedValue(weekdays)
})

afterEach(cleanup)

/**
 * The way back is a link rather than a button. The editor is a route now, so
 * leaving it is a navigation: a link carries the address, opens in a new tab and
 * survives the browser's own back button, none of which an onClick handler does.
 */
test('shows the pattern name as the heading and a way back to the list', async () => {
  renderEditor()

  expect(await screen.findByRole('heading', { level: 1, name: weekdays.name })).toBeDefined()
  expect(screen.getByRole('link', { name: /availability/i }).getAttribute('href')).toBe(LIST)
})

/**
 * The server's own sentence, not one made up here. The read is the pattern list —
 * there is no single-pattern endpoint — so the failure that reaches this screen is
 * whatever that list answered with, and the only text saying what went wrong.
 */
test("reports the server's own words when the pattern will not load", async () => {
  const said = 'The patterns could not be read.'
  vi.mocked(listPatterns).mockRejectedValue(new ApiError(UNAVAILABLE, 'patterns_unavailable', said))

  renderEditor()

  expect((await screen.findByRole('alert')).textContent).toContain(said)
})

/**
 * A typed, stale or deleted id, and none of them is a crash. The address is
 * shareable now, so an id that matches nothing is an ordinary arrival — a bookmark
 * to a pattern deleted in another tab reaches exactly this state.
 */
test('an id that matches no pattern says so and keeps the way back', async () => {
  vi.mocked(listPatterns).mockResolvedValue([])

  renderEditor()

  expect(await screen.findByText(/no longer there/i)).toBeDefined()
  expect(screen.queryByRole('alert')).toBeNull()
  expect(screen.getByRole('link', { name: /availability/i }).getAttribute('href')).toBe(LIST)
})

/**
 * What is pinned is the exact set that reaches the server: Monday's hours on the
 * days that were already open, Thursday to Sunday still absent because absent IS
 * closed, and one request rather than one per day — the endpoint takes the whole
 * week, so a loop over the open days would be six ways to end up half applied.
 */
test("uses one day's hours on every open day, and leaves the closed days closed", async () => {
  patternWith([
    { id: 'r1', isoDayOfWeek: MONDAY, startMinute: NINE_AM, endMinute: FIVE_PM },
    { id: 'r2', isoDayOfWeek: TUESDAY, startMinute: TEN_AM, endMinute: FOUR_PM },
    { id: 'r3', isoDayOfWeek: WEDNESDAY, startMinute: TEN_AM, endMinute: FOUR_PM },
  ])

  renderEditor()

  fireEvent.click(
    await screen.findByRole('button', { name: 'Monday: Use these hours on every open day' }),
  )
  fireEvent.click(screen.getByRole('button', { name: 'Save weekly hours' }))

  await waitFor(() => expect(replacePatternRules).toHaveBeenCalledTimes(1))
  expect(replacePatternRules).toHaveBeenCalledWith(PATTERN_ID, [
    { isoDayOfWeek: MONDAY, startMinute: NINE_AM, endMinute: FIVE_PM },
    { isoDayOfWeek: TUESDAY, startMinute: NINE_AM, endMinute: FIVE_PM },
    { isoDayOfWeek: WEDNESDAY, startMinute: NINE_AM, endMinute: FIVE_PM },
  ])
})

/**
 * A split day, made here rather than merely tolerated. The booking engine honours
 * several windows on one day and the server accepts two that touch, so a lunch
 * break is two rules and not something the pane flattens. Nothing is sent while
 * the times are typed: a pattern in use is live, and a half-typed window would be
 * an hour on offer.
 */
test('a second window on one day is sent as two windows, and not before the save', async () => {
  patternWith([{ id: 'r1', isoDayOfWeek: MONDAY, startMinute: NINE_AM, endMinute: NOON }])

  renderEditor()

  fireEvent.click(await screen.findByRole('button', { name: 'Add window to Monday' }))
  fireEvent.change(screen.getByLabelText('Monday, window 2, opens at'), {
    target: { value: '13:00' },
  })
  fireEvent.change(screen.getByLabelText('Monday, window 2, closes at'), {
    target: { value: '17:00' },
  })

  expect(replacePatternRules).not.toHaveBeenCalled()

  fireEvent.click(screen.getByRole('button', { name: 'Save weekly hours' }))

  await waitFor(() => expect(replacePatternRules).toHaveBeenCalledTimes(1))
  expect(replacePatternRules).toHaveBeenCalledWith(PATTERN_ID, [
    { isoDayOfWeek: MONDAY, startMinute: NINE_AM, endMinute: NOON },
    { isoDayOfWeek: MONDAY, startMinute: ONE_PM, endMinute: FIVE_PM },
  ])
})

/**
 * Closing a day is removing its last window, and there is no second control for
 * it. A day with no windows is a closed day — the row says so and offers to open
 * it again — and the request says the same thing by leaving the day out.
 */
test("removing a day's last window closes that day", async () => {
  patternWith([
    { id: 'r1', isoDayOfWeek: MONDAY, startMinute: NINE_AM, endMinute: FIVE_PM },
    { id: 'r2', isoDayOfWeek: TUESDAY, startMinute: TEN_AM, endMinute: FOUR_PM },
  ])

  renderEditor()

  fireEvent.click(await screen.findByRole('button', { name: 'Remove Monday window 1' }))
  expect(screen.getByRole('button', { name: 'Open Monday' })).toBeDefined()

  fireEvent.click(screen.getByRole('button', { name: 'Save weekly hours' }))

  await waitFor(() => expect(replacePatternRules).toHaveBeenCalledTimes(1))
  expect(replacePatternRules).toHaveBeenCalledWith(PATTERN_ID, [
    { isoDayOfWeek: TUESDAY, startMinute: TEN_AM, endMinute: FOUR_PM },
  ])
})

/**
 * Overlap is refused by the endpoint and by nothing on this page, deliberately:
 * the same bound guards the booking engine, and a second copy here is the copy
 * that goes stale. So the refusal arrives as the server's own prose, under the
 * control that asked for it.
 */
test('a refused save shows what the server said about it', async () => {
  const said =
    'Another window on that day already covers part of those hours. Overlapping windows offer the same time twice.'
  patternWith([{ id: 'r1', isoDayOfWeek: MONDAY, startMinute: NINE_AM, endMinute: FIVE_PM }])
  vi.mocked(replacePatternRules).mockRejectedValue(
    new ApiError(CONFLICT, 'rule_overlaps', said),
  )

  renderEditor()

  fireEvent.click(await screen.findByRole('button', { name: 'Add window to Monday' }))
  fireEvent.change(screen.getByLabelText('Monday, window 2, opens at'), {
    target: { value: '10:00' },
  })
  fireEvent.change(screen.getByLabelText('Monday, window 2, closes at'), {
    target: { value: '16:00' },
  })
  fireEvent.click(screen.getByRole('button', { name: 'Save weekly hours' }))

  expect((await screen.findByRole('alert')).textContent).toContain(said)
})

/**
 * Every class this page renders has to be one the stylesheet gained. A name that
 * only exists in the markup styles nothing, and it fails silently: the two panes
 * simply stop being two panes, at a width no unit test can see.
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

/**
 * Finding 13. `IsoDayOfWeek` carries no CHECK constraint, so a row outside 1-7
 * is storable, and `week[rule.isoDayOfWeek - MONDAY]` indexed straight into
 * `undefined` and threw on `.push` — taking the whole editor down rather than
 * losing one rule. `AvailabilityQuery.cs:108` guards the identical lookup on the
 * server and says why; this is the same decision on the client.
 */
test('a rule on a day of the week that does not exist drops rather than taking the editor down', async () => {
  patternWith([
    { id: 'r0', isoDayOfWeek: DAY_THAT_IS_NOT_A_DAY, startMinute: NINE_AM, endMinute: FIVE_PM },
    { id: 'r1', isoDayOfWeek: TUESDAY, startMinute: NINE_AM, endMinute: FIVE_PM },
  ])

  renderEditor()

  expect(await screen.findByLabelText('Tuesday, window 1, opens at')).toBeDefined()
})

// --- renaming ---------------------------------------------------------------
//
// The name is edited HERE and not on the availability list. This page is where a
// pattern is worked on, so its title is the name being read at the moment
// somebody decides it is wrong; the list's job is to get you to the pattern.

const RENAMED = 'Weekday mornings'

/** Opens the editor on the page title. */
async function openRenameEditor() {
  fireEvent.click(await screen.findByRole('button', { name: weekdays.name }))
  return screen.getByRole('textbox', { name: 'Pattern name' })
}

test('clicking the title opens an editor holding the current name', async () => {
  renderEditor()

  expect(await openRenameEditor()).toHaveProperty('value', weekdays.name)
})

test('saving renames the pattern and retitles the page', async () => {
  vi.mocked(renamePattern).mockResolvedValue({ ...weekdays, name: RENAMED })
  renderEditor()

  const field = await openRenameEditor()
  fireEvent.change(field, { target: { value: RENAMED } })
  fireEvent.click(screen.getByRole('button', { name: 'Save' }))

  await waitFor(() => expect(vi.mocked(renamePattern)).toHaveBeenCalledWith(PATTERN_ID, RENAMED))
  expect(await screen.findByRole('heading', { level: 1, name: RENAMED })).toBeDefined()
})

test('cancelling reverts the name and writes nothing', async () => {
  renderEditor()

  const field = await openRenameEditor()
  fireEvent.change(field, { target: { value: RENAMED } })
  fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))

  expect(vi.mocked(renamePattern)).not.toHaveBeenCalled()
  expect(await screen.findByRole('heading', { level: 1, name: weekdays.name })).toBeDefined()
})

// The property this editor has and a blur-to-save one does not. A stray click
// elsewhere on the page must not commit a half-typed name, so blur neither saves
// nor closes — the operator chooses, every time.
test('clicking away neither saves nor closes the editor', async () => {
  renderEditor()

  const field = await openRenameEditor()
  fireEvent.change(field, { target: { value: RENAMED } })
  fireEvent.blur(field)

  expect(vi.mocked(renamePattern)).not.toHaveBeenCalled()
  expect(screen.getByRole('textbox', { name: 'Pattern name' })).toHaveProperty('value', RENAMED)
})

test('a refused rename keeps the editor open and shows the reason', async () => {
  vi.mocked(renamePattern).mockRejectedValue(
    new ApiError(CONFLICT, 'pattern_name_taken', 'Another pattern is already called that.'),
  )
  renderEditor()

  const field = await openRenameEditor()
  fireEvent.change(field, { target: { value: RENAMED } })
  fireEvent.click(screen.getByRole('button', { name: 'Save' }))

  expect((await screen.findByRole('alert')).textContent).toContain('already called that')
  expect(screen.getByRole('textbox', { name: 'Pattern name' })).toHaveProperty('value', RENAMED)
})
