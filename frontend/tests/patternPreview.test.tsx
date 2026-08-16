import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'

// ApiError comes from the real module and is never mocked: `Failure` reads a
// rejection through `asApiError`, which recognises it with `instanceof`. A plain
// object would take the "carries no status" path and the server's own sentence
// would be replaced with "could not reach the server".
import { ApiError, getSlots } from '../src/lib/api'
import { listLinks, type AdminLink } from '../src/routes/admin/adminLinks'
import { getSettings } from '../src/routes/admin/adminSettings'
import PatternPreview from '../src/routes/admin/PatternPreview'
import {
  removePatternOverride,
  savePatternOverride,
  type Pattern,
} from '../src/routes/admin/patterns'

vi.mock('../src/lib/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/lib/api')>()),
  getSlots: vi.fn(),
}))
vi.mock('../src/routes/admin/adminLinks', () => ({ listLinks: vi.fn() }))
vi.mock('../src/routes/admin/adminSettings', () => ({ getSettings: vi.fn() }))
vi.mock('../src/routes/admin/patterns', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/patterns')>()),
  savePatternOverride: vi.fn(),
  removePatternOverride: vi.fn(),
}))

// Same file the running app loads. Whatever this pane adds to it, the
// stylesheet stays the authority on which class names exist.
const stylesheet = resolve(process.cwd(), '../backend/src/Kairos.Api/wwwroot/kairos.css')

const PATTERN_ID = 'b0a5f0e2-0000-7000-8000-000000000001'
const UNAVAILABLE = 503

// Minutes from local midnight, which is what a rule and an override hold.
const MINUTES_PER_HOUR = 60
const NINE_AM = 9 * MINUTES_PER_HOUR
const ONE_PM = 13 * MINUTES_PER_HOUR
const FIVE_PM = 17 * MINUTES_PER_HOUR

// ISO-8601: 1 is Monday, 7 is Sunday.
const TUESDAY = 2

// Noon UTC, so the operator's own date is 15 August 2026 in every zone this
// suite could be run in — the preview opens on the month it finds today in.
const TODAY = '2026-08-15T12:00:00Z'
// August 2026: the 18th and the 25th are Tuesdays, the 23rd a Sunday.
const A_TUESDAY = '2026-08-18'
const A_SUNDAY = '2026-08-23'

const weekdays: Pattern = {
  id: PATTERN_ID,
  name: 'Weekdays',
  rules: [{ id: 'r1', isoDayOfWeek: TUESDAY, startMinute: NINE_AM, endMinute: FIVE_PM }],
  overrides: [],
  usedBy: [],
}

const mentorship: AdminLink = {
  id: 'l1',
  slug: 'mentorship',
  name: 'Mentorship',
  description: '',
  patternId: PATTERN_ID,
  patternName: weekdays.name,
  active: true,
  durations: [30],
  marginMinutes: null,
  minNoticeMinutes: null,
  dailyCap: null,
  startIncrementMinutes: null,
}

/**
 * One square of the month grid, named by its date and whatever follows it.
 * Anchored on the weekday the grid puts first, because an exception's own
 * button carries the same date under "Show" and would match a bare date.
 */
function day(name: string): HTMLElement {
  return screen.getByRole('button', { name: new RegExp(`^\\w+ ${name}`) })
}

/** A pattern one link stands on, so the times picker has something to offer. */
function usedByMentorship(overrides: Pattern['overrides'] = []): Pattern {
  return { ...weekdays, overrides, usedBy: [mentorship.name] }
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.useFakeTimers({ toFake: ['Date'] })
  vi.setSystemTime(new Date(TODAY))

  vi.mocked(listLinks).mockResolvedValue({
    links: [mentorship],
    patterns: [{ id: PATTERN_ID, name: weekdays.name }],
    globals: {
      marginMinutes: 0,
      minNoticeMinutes: 0,
      dailyCap: 0,
      startIncrementMinutes: 30,
    },
  })
  vi.mocked(getSettings).mockResolvedValue({
    timeZone: 'Europe/Warsaw',
    signedInAs: null,
    marginMinutes: 0,
    minNoticeMinutes: 0,
    dailyCap: 0,
    startIncrementMinutes: 30,
    overrides: { marginMinutes: 0, minNoticeMinutes: 0, dailyCap: 0, startIncrementMinutes: 0 },
  })
})

afterEach(() => {
  vi.useRealTimers()
  cleanup()
})

/**
 * The act the pane exists to make possible, and both halves of it.
 *
 * A holiday is blocked by clicking it and unblocked by clicking it again —
 * there is no second control and no dialog between the two, because the inverse
 * of the click IS the click. What is pinned is the pair of requests: an
 * override with no hours in it, and then its removal, which are two different
 * endpoints and mean two different things to the booking engine.
 */
test('clicking a date blocks it, and clicking it again puts it back on the week', async () => {
  const blocked = { date: A_TUESDAY, startMinute: null, endMinute: null }
  vi.mocked(savePatternOverride).mockResolvedValue({ ...weekdays, overrides: [blocked] })
  vi.mocked(removePatternOverride).mockResolvedValue(weekdays)

  render(<PatternPreview pattern={weekdays} />)

  // A Tuesday, so the weekly rule has it open before the click.
  expect(day('18 August').getAttribute('data-day-state')).toBe('open')

  fireEvent.click(day('18 August'))

  await waitFor(() =>
    expect(day('18 August').getAttribute('data-day-state')).toBe('blocked'),
  )
  expect(savePatternOverride).toHaveBeenCalledWith(PATTERN_ID, A_TUESDAY, {
    startMinute: null,
    endMinute: null,
  })

  fireEvent.click(day('18 August'))

  await waitFor(() => expect(removePatternOverride).toHaveBeenCalledWith(PATTERN_ID, A_TUESDAY))
})

/**
 * A date with its own hours is an open day and the grid draws it as one, so the
 * list is the only place the third state is legible. Reading one must not
 * change it: the square's click is a toggle, and reaching a replacement window
 * through it would clear the window on the way in.
 */
test('a date with its own hours is readable without being changed', () => {
  const ownHours = { date: A_SUNDAY, startMinute: NINE_AM, endMinute: ONE_PM }

  render(<PatternPreview pattern={usedByMentorship([ownHours])} />)

  expect(screen.getByText('09:00–13:00')).toBeDefined()

  fireEvent.click(screen.getByRole('button', { name: 'Show Sunday 23 August' }))

  expect(savePatternOverride).not.toHaveBeenCalled()
  expect(removePatternOverride).not.toHaveBeenCalled()
  expect(
    screen.getByLabelText(/23 August, opens at/).getAttribute('value'),
  ).toBe('09:00')
})

/**
 * The failure this whole surface was built to stop telling. A month drawn with
 * no counts in it is the same picture as a link that offers nothing, so a slots
 * request that did not answer must say so rather than hand back a calendar the
 * operator would read as an outage.
 */
test('a times read that fails says so rather than drawing an empty month', async () => {
  const said = 'The times could not be worked out.'
  vi.mocked(getSlots).mockRejectedValue(new ApiError(UNAVAILABLE, 'slots_unavailable', said))

  render(<PatternPreview pattern={usedByMentorship()} />)

  fireEvent.change(await screen.findByLabelText('Show'), {
    target: { value: mentorship.slug },
  })

  expect((await screen.findByRole('alert')).textContent).toContain(said)
  expect(screen.queryByRole('group')).toBeNull()
})

/**
 * Every class this pane renders has to be one the stylesheet already has. A name
 * that exists only in the markup styles nothing, and it fails silently — at a
 * width no unit test can see. Rendered with a link and both kinds of override,
 * so the picker and the exception list are in the markup being checked.
 */
test('every class the preview renders exists in kairos.css', async () => {
  const css = readFileSync(stylesheet, 'utf8')
  const { container } = render(
    <PatternPreview
      pattern={usedByMentorship([
        { date: A_TUESDAY, startMinute: null, endMinute: null },
        { date: A_SUNDAY, startMinute: NINE_AM, endMinute: ONE_PM },
      ])}
    />,
  )

  fireEvent.click(await screen.findByRole('button', { name: 'Show Sunday 23 August' }))

  const used = new Set(
    [...container.querySelectorAll('[class]')].flatMap((element) => [...element.classList]),
  )

  expect([...used].filter((name) => !css.includes(`.${name}`))).toEqual([])
})

/**
 * The preview's own reading of the week, checked against the one the pane draws
 * a link's times from. Two implementations that must not drift: this one says
 * which days the pattern opens, the server says which of them a link can
 * actually be booked on, and the second is a subset of the first — never a day
 * the pattern never opened.
 */
test('the days a link offers times on are days the pattern opens', async () => {
  vi.mocked(getSlots).mockResolvedValue([
    // 09:00 Warsaw on two Tuesdays, which is 07:00 UTC.
    '2026-08-18T07:00:00Z',
    '2026-08-25T07:00:00Z',
  ])

  render(<PatternPreview pattern={usedByMentorship()} />)

  fireEvent.change(await screen.findByLabelText('Show'), {
    target: { value: mentorship.slug },
  })

  await waitFor(() => expect(day('18 August, open, 1 time available')).toBeDefined())
  expect(day('25 August, open, 1 time available')).toBeDefined()
  // A Sunday the pattern never opens, and the link has nothing on it either.
  expect(day('23 August, closed')).toBeDefined()
})
