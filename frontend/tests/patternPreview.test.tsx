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
const NOON = 12 * MINUTES_PER_HOUR
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
// The operator's own date, which is a Saturday, and the Thursday they blocked
// by accident nine days before it.
const TODAYS_DATE = '2026-08-15'
const A_PAST_THURSDAY = '2026-08-06'

/** The picker's own value for "no link", which the pane names DAYS_ONLY. */
const DAYS_ONLY = ''

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

/**
 * A promise whose settlement the test controls, for putting a write genuinely
 * in flight rather than merely mocking its eventual answer. `mockResolvedValue`
 * settles before the test can act in between — this is what lets a click land
 * while the request before it is still unanswered.
 */
function deferred<T>(): { promise: Promise<T>; resolve: (value: T) => void } {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((res) => {
    resolve = res
  })
  return { promise, resolve }
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
    proposalHoldHours: 48,
    overrides: { marginMinutes: 0, minNoticeMinutes: 0, dailyCap: 0, startIncrementMinutes: 0 },
  })
})

afterEach(() => {
  vi.useRealTimers()
  cleanup()
})

// August 2026: the 23rd is a Sunday, which the weekly rules leave closed, so
// nothing but an exception can put hours on it.
const A_SUNDAY_LABEL = '23 August'
const OWN_HOURS = { date: A_SUNDAY, windows: [{ startMinute: NINE_AM, endMinute: NOON }] }

test('clicking a date square selects it and writes nothing', () => {
  render(<PatternPreview pattern={usedByMentorship()} />)

  fireEvent.click(day(A_SUNDAY_LABEL))

  // The defect this replaces. The square used to save on click and do one of
  // two opposite things — block a clean date, or delete an exception —
  // decided by state the operator could not see before pressing.
  expect(savePatternOverride).not.toHaveBeenCalled()
  expect(removePatternOverride).not.toHaveBeenCalled()
  expect(screen.getByRole('radiogroup', { name: new RegExp(A_SUNDAY_LABEL) })).toBeDefined()
})

test('a date with its own windows still has them after its square is clicked', () => {
  render(<PatternPreview pattern={usedByMentorship([OWN_HOURS])} />)

  fireEvent.click(day(A_SUNDAY_LABEL))

  // The consequence of that defect: one click destroyed a typed morning and
  // afternoon, with no confirmation and nothing to undo it.
  expect(removePatternOverride).not.toHaveBeenCalled()
  expect(
    screen.getByLabelText(new RegExp(`${A_SUNDAY_LABEL}, window 1, opens at`)).getAttribute('value'),
  ).toBe('09:00')
})

test('picking Blocked writes immediately', async () => {
  const blocked = { date: A_SUNDAY, windows: [] }
  vi.mocked(savePatternOverride).mockResolvedValue(usedByMentorship([blocked]))

  render(<PatternPreview pattern={usedByMentorship()} />)
  fireEvent.click(day(A_SUNDAY_LABEL))
  fireEvent.click(screen.getByRole('radio', { name: 'Blocked' }))

  await waitFor(() => expect(savePatternOverride).toHaveBeenCalledWith(PATTERN_ID, A_SUNDAY, []))
})

test('picking Weekly writes immediately', async () => {
  vi.mocked(removePatternOverride).mockResolvedValue(usedByMentorship())

  render(<PatternPreview pattern={usedByMentorship([OWN_HOURS])} />)
  fireEvent.click(day(A_SUNDAY_LABEL))
  fireEvent.click(screen.getByRole('radio', { name: 'Weekly' }))

  await waitFor(() => expect(removePatternOverride).toHaveBeenCalledWith(PATTERN_ID, A_SUNDAY))
})

test('picking Own hours writes nothing until Save this date', async () => {
  vi.mocked(savePatternOverride).mockResolvedValue(usedByMentorship([OWN_HOURS]))

  render(<PatternPreview pattern={usedByMentorship()} />)
  fireEvent.click(day(A_SUNDAY_LABEL))
  fireEvent.click(screen.getByRole('radio', { name: 'Own hours' }))

  // A pattern in use is live. A keystroke that widened a window would be an
  // hour on offer the moment it was typed, and a half-typed one would come
  // back refused as a window ending before it starts.
  expect(savePatternOverride).not.toHaveBeenCalled()

  fireEvent.change(screen.getByLabelText(new RegExp(`${A_SUNDAY_LABEL}, window 1, opens at`)), {
    target: { value: '09:00' },
  })
  fireEvent.change(screen.getByLabelText(new RegExp(`${A_SUNDAY_LABEL}, window 1, closes at`)), {
    target: { value: '12:00' },
  })
  expect(savePatternOverride).not.toHaveBeenCalled()

  fireEvent.click(screen.getByRole('button', { name: 'Save this date' }))

  await waitFor(() =>
    expect(savePatternOverride).toHaveBeenCalledWith(PATTERN_ID, A_SUNDAY, [
      { startMinute: NINE_AM, endMinute: NOON },
    ]),
  )
})

/**
 * Manual activation, checked at the one point it matters: moving focus across
 * the group must never itself be a write. The APG default moves focus and
 * selects together, which here would mean an arrow key fires a save at a
 * pattern links stand on — this is the assertion that would catch a
 * regression back to that default, so it is explicit rather than implied by
 * the tests that check what a deliberate pick does.
 */
test('arrow key moves focus between options and writes nothing', () => {
  render(<PatternPreview pattern={usedByMentorship()} />)
  fireEvent.click(day(A_SUNDAY_LABEL))

  const weekly = screen.getByRole('radio', { name: 'Weekly' })
  const ownHours = screen.getByRole('radio', { name: 'Own hours' })
  weekly.focus()
  expect(document.activeElement).toBe(weekly)

  fireEvent.keyDown(weekly, { key: 'ArrowRight' })

  expect(document.activeElement).toBe(ownHours)
  expect(savePatternOverride).not.toHaveBeenCalled()
  expect(removePatternOverride).not.toHaveBeenCalled()
})

test('focus wraps at both ends of the group', () => {
  render(<PatternPreview pattern={usedByMentorship()} />)
  fireEvent.click(day(A_SUNDAY_LABEL))

  const weekly = screen.getByRole('radio', { name: 'Weekly' })
  const blocked = screen.getByRole('radio', { name: 'Blocked' })

  weekly.focus()
  fireEvent.keyDown(weekly, { key: 'ArrowLeft' })
  expect(document.activeElement).toBe(blocked)

  blocked.focus()
  fireEvent.keyDown(blocked, { key: 'ArrowRight' })
  expect(document.activeElement).toBe(weekly)
})

test('Home and End move focus straight to the first and last option', () => {
  render(<PatternPreview pattern={usedByMentorship()} />)
  fireEvent.click(day(A_SUNDAY_LABEL))

  const weekly = screen.getByRole('radio', { name: 'Weekly' })
  const ownHours = screen.getByRole('radio', { name: 'Own hours' })
  const blocked = screen.getByRole('radio', { name: 'Blocked' })

  ownHours.focus()
  fireEvent.keyDown(ownHours, { key: 'End' })
  expect(document.activeElement).toBe(blocked)

  fireEvent.keyDown(blocked, { key: 'Home' })
  expect(document.activeElement).toBe(weekly)
})

test('Space selects the focused option and writes', async () => {
  const blocked = { date: A_SUNDAY, windows: [] }
  vi.mocked(savePatternOverride).mockResolvedValue(usedByMentorship([blocked]))

  render(<PatternPreview pattern={usedByMentorship()} />)
  fireEvent.click(day(A_SUNDAY_LABEL))

  const target = screen.getByRole('radio', { name: 'Blocked' })
  target.focus()
  fireEvent.keyDown(target, { key: ' ' })

  await waitFor(() => expect(savePatternOverride).toHaveBeenCalledWith(PATTERN_ID, A_SUNDAY, []))
})

test('Enter selects the focused option and writes', async () => {
  vi.mocked(removePatternOverride).mockResolvedValue(usedByMentorship())

  render(<PatternPreview pattern={usedByMentorship([OWN_HOURS])} />)
  fireEvent.click(day(A_SUNDAY_LABEL))

  const target = screen.getByRole('radio', { name: 'Weekly' })
  target.focus()
  fireEvent.keyDown(target, { key: 'Enter' })

  await waitFor(() => expect(removePatternOverride).toHaveBeenCalledWith(PATTERN_ID, A_SUNDAY))
})

/**
 * Roving tabindex, and only one stop: whichever option is actually selected,
 * so Tab enters and leaves the group in one hop instead of walking all three.
 */
test('only the selected option is a tab stop', () => {
  render(<PatternPreview pattern={usedByMentorship([OWN_HOURS])} />)
  fireEvent.click(day(A_SUNDAY_LABEL))

  expect(screen.getByRole('radio', { name: 'Weekly' }).getAttribute('tabindex')).toBe('-1')
  expect(screen.getByRole('radio', { name: 'Own hours' }).getAttribute('tabindex')).toBe('0')
  expect(screen.getByRole('radio', { name: 'Blocked' }).getAttribute('tabindex')).toBe('-1')
})

/**
 * The race: Blocked is picked and its write left hanging, then Own hours is
 * picked before that write lands. `editingOwnHours` has no write of its own
 * to guard it, so without the fix this is exactly the sequence that let it
 * win — the control would show rows for a date the server had not yet agreed
 * was anything but Blocked. `mockResolvedValue` cannot reach this sequence at
 * all, because it settles before a second click has anywhere to land; the
 * deferred promise is what keeps the first write genuinely open.
 */
test('a write in flight blocks the other picks, and the control agrees with the data once it lands', async () => {
  const blocked = { date: A_SUNDAY, windows: [] }
  const { promise, resolve } = deferred<Pattern>()
  vi.mocked(savePatternOverride).mockReturnValue(promise)

  render(<PatternPreview pattern={usedByMentorship()} />)
  fireEvent.click(day(A_SUNDAY_LABEL))
  fireEvent.click(screen.getByRole('radio', { name: 'Blocked' }))

  // The write has fired and is still unanswered — the control has not yet
  // heard back, so it still reads Weekly, the state it was in before the
  // click. Own hours is picked through both entry points a click or a
  // keystroke could reach it by.
  const ownHours = screen.getByRole('radio', { name: 'Own hours' })
  expect(ownHours.hasAttribute('disabled')).toBe(true)
  fireEvent.click(ownHours)
  fireEvent.keyDown(ownHours, { key: ' ' })

  // Neither moved the control onto Own hours, and neither sent a second
  // write — `pickBlocked`'s is still the only one out.
  expect(screen.getByRole('radio', { name: 'Own hours' }).getAttribute('aria-checked')).toBe(
    'false',
  )
  expect(
    screen.queryByLabelText(new RegExp(`${A_SUNDAY_LABEL}, window 1, opens at`)),
  ).toBeNull()
  expect(savePatternOverride).toHaveBeenCalledTimes(1)

  resolve(usedByMentorship([blocked]))

  // Once the write lands the control still agrees with the data — Blocked,
  // never having shown Own hours for a date that was never anything else.
  await waitFor(() =>
    expect(screen.getByRole('radio', { name: 'Blocked' }).getAttribute('aria-checked')).toBe(
      'true',
    ),
  )
  expect(
    screen.queryByLabelText(new RegExp(`${A_SUNDAY_LABEL}, window 1, opens at`)),
  ).toBeNull()
})

/**
 * The ordinary failure this branch had no test for: the server refuses, or
 * the network drops, and the pick that caused it never happened. `write`'s
 * own catch is what this pins — `setFailure` for `Failure` to surface,
 * `setSaving(false)` so the control is not left disabled over a write that
 * is no longer in flight, and `overrides` untouched, so the control settles
 * back on what the data actually says rather than on what was asked for.
 */
test('a rejected write surfaces the failure and leaves the control usable', async () => {
  const said = 'The date could not be blocked.'
  vi.mocked(savePatternOverride).mockRejectedValue(
    new ApiError(UNAVAILABLE, 'pattern_invalid', said),
  )

  render(<PatternPreview pattern={usedByMentorship()} />)
  fireEvent.click(day(A_SUNDAY_LABEL))
  fireEvent.click(screen.getByRole('radio', { name: 'Blocked' }))

  expect((await screen.findByRole('alert')).textContent).toContain(said)

  // Usable again — nothing stayed disabled over a write that already failed.
  expect(screen.getByRole('radio', { name: 'Blocked' }).hasAttribute('disabled')).toBe(false)
  expect(screen.getByRole('radio', { name: 'Weekly' }).hasAttribute('disabled')).toBe(false)
  expect(screen.getByRole('radio', { name: 'Own hours' }).hasAttribute('disabled')).toBe(false)

  // Still Weekly: the pick never landed, so the control reads what the data
  // still says rather than what was asked for and refused.
  expect(screen.getByRole('radio', { name: 'Weekly' }).getAttribute('aria-checked')).toBe('true')
  expect(screen.getByRole('radio', { name: 'Blocked' }).getAttribute('aria-checked')).toBe('false')
})

/**
 * A date with its own hours is an open day and the grid draws it as one, so
 * the list is the only place the third state is legible before it is opened.
 * Opened through the list rather than the square, which is the other door
 * into the same panel — and either way, opening a date must never change it.
 */
test('a date with its own hours is readable without being changed', () => {
  const ownHours = { date: A_SUNDAY, windows: [{ startMinute: NINE_AM, endMinute: ONE_PM }] }

  render(<PatternPreview pattern={usedByMentorship([ownHours])} />)

  expect(screen.getByText('09:00–13:00')).toBeDefined()

  fireEvent.click(screen.getByRole('button', { name: 'Show Sunday 23 August' }))

  expect(savePatternOverride).not.toHaveBeenCalled()
  expect(removePatternOverride).not.toHaveBeenCalled()
  expect(
    screen.getByLabelText(/23 August, window 1, opens at/).getAttribute('value'),
  ).toBe('09:00')
})

/**
 * The reported defect, at the surface the operator met it on: 6 August was
 * blocked from this grid on the 15th, and no click on a day that has already
 * happened can change what any invitee was offered. Today stays live — the rest
 * of it is still ahead, and closing it would take a working day away to fix an
 * untidiness in the past.
 */
test('a date before today writes nothing, and today is still live', async () => {
  vi.mocked(savePatternOverride).mockResolvedValue(weekdays)

  render(<PatternPreview pattern={weekdays} />)
  // Waited for, because the zone that decides which dates have passed is the
  // organizer's and arrives from settings — the browser's is only the guess the
  // first paint is drawn on.
  await waitFor(() => expect(getSettings).toHaveBeenCalled())

  // A Tuesday, so the weekly rule opened it and only its date closes it.
  expect(day('11 August').hasAttribute('disabled')).toBe(true)

  fireEvent.click(day('11 August'))

  expect(savePatternOverride).not.toHaveBeenCalled()
  expect(removePatternOverride).not.toHaveBeenCalled()
  // No panel: the control belongs to a selected date, and none was selected.
  expect(screen.queryByRole('radiogroup')).toBeNull()

  // Today itself carries no such attribute. Selecting it opens its own
  // control, and that control can still write — unlike the day before it.
  fireEvent.click(day('15 August'))
  fireEvent.click(screen.getByRole('radio', { name: 'Blocked' }))

  await waitFor(() =>
    expect(savePatternOverride).toHaveBeenCalledWith(PATTERN_ID, TODAYS_DATE, []),
  )
})

/**
 * History is shown, not hidden. The operator's accidental block on 6 August is
 * still in the pattern and still says so — in the list, which is where a date's
 * own hours have always been read — while the date itself carries no control
 * that could change it.
 */
test('an override on a past date is still readable and no longer editable', async () => {
  render(
    <PatternPreview
      pattern={{
        ...weekdays,
        overrides: [{ date: A_PAST_THURSDAY, windows: [] }],
      }}
    />,
  )
  await waitFor(() => expect(getSettings).toHaveBeenCalled())

  expect(screen.getByText(/Blocked · already passed/)).toBeDefined()
  expect(screen.queryByRole('button', { name: 'Show Thursday 6 August' })).toBeNull()
  expect(day('6 August').hasAttribute('disabled')).toBe(true)
})

/**
 * The second defect. The fields used to open on whatever an unset time input
 * shows — a start equal to its end, which the server refuses as
 * `window_inverted` — so moving one end of a day meant setting both. They open
 * on the hours that weekday already offers, and one edit is one edit again.
 */
test("a date's hours open on the week's, so moving one end is enough", async () => {
  const trimmed = { date: A_TUESDAY, windows: [{ startMinute: NINE_AM, endMinute: ONE_PM }] }
  vi.mocked(savePatternOverride).mockResolvedValue({ ...weekdays, overrides: [trimmed] })

  render(<PatternPreview pattern={weekdays} />)

  // Selecting the date writes nothing; picking Own hours is what reveals the
  // fields. What they carry is the point: the hours the date would offer if
  // it followed the week, open before anything has been typed.
  fireEvent.click(day('18 August'))
  fireEvent.click(screen.getByRole('radio', { name: 'Own hours' }))

  expect(screen.getByLabelText(/18 August, window 1, opens at/).getAttribute('value')).toBe('09:00')
  expect(screen.getByLabelText(/18 August, window 1, closes at/).getAttribute('value')).toBe('17:00')

  fireEvent.change(screen.getByLabelText(/18 August, window 1, closes at/), {
    target: { value: '13:00' },
  })
  fireEvent.click(screen.getByRole('button', { name: 'Save this date' }))

  await waitFor(() =>
    expect(savePatternOverride).toHaveBeenCalledWith(PATTERN_ID, A_TUESDAY, [
      { startMinute: NINE_AM, endMinute: ONE_PM },
    ]),
  )
})

/**
 * The feature, and the fact that it is ONE request.
 *
 * The endpoint replaces a date's windows entire, so a morning sent and then an
 * afternoon sent would leave the date offering the morning alone in between —
 * live, on a pattern links stand on. The draft is what makes the two one act,
 * and the press is what sends it.
 */
test('a date can offer a morning and an afternoon, and it saves in one request', async () => {
  const morning = { date: A_SUNDAY, windows: [{ startMinute: NINE_AM, endMinute: NOON }] }
  vi.mocked(savePatternOverride).mockResolvedValue(usedByMentorship([morning]))

  render(<PatternPreview pattern={usedByMentorship([morning])} />)

  // Through the list, the other door into the same panel as the square.
  fireEvent.click(screen.getByRole('button', { name: 'Show Sunday 23 August' }))
  fireEvent.click(screen.getByRole('button', { name: 'Add window to Sunday 23 August' }))

  fireEvent.change(screen.getByLabelText(/23 August, window 2, opens at/), {
    target: { value: '13:00' },
  })
  fireEvent.change(screen.getByLabelText(/23 August, window 2, closes at/), {
    target: { value: '17:00' },
  })
  fireEvent.click(screen.getByRole('button', { name: 'Save this date' }))

  await waitFor(() =>
    expect(savePatternOverride).toHaveBeenCalledWith(PATTERN_ID, A_SUNDAY, [
      { startMinute: NINE_AM, endMinute: NOON },
      { startMinute: ONE_PM, endMinute: FIVE_PM },
    ]),
  )
  expect(savePatternOverride).toHaveBeenCalledTimes(1)
})

/**
 * The read half of the same round trip. A split date is drawn as an ordinary
 * open square, so the exceptions list is the only place both windows are
 * legible — and opening it must offer both rows, or editing the morning of a
 * split date would silently propose dropping its afternoon.
 */
test('a date that already holds two windows lists both and opens both', () => {
  const split = {
    date: A_SUNDAY,
    windows: [
      { startMinute: NINE_AM, endMinute: NOON },
      { startMinute: ONE_PM, endMinute: FIVE_PM },
    ],
  }

  render(<PatternPreview pattern={usedByMentorship([split])} />)

  expect(screen.getByText('09:00–12:00, 13:00–17:00')).toBeDefined()

  fireEvent.click(screen.getByRole('button', { name: 'Show Sunday 23 August' }))

  expect(
    screen.getByLabelText(/23 August, window 1, opens at/).getAttribute('value'),
  ).toBe('09:00')
  expect(
    screen.getByLabelText(/23 August, window 2, opens at/).getAttribute('value'),
  ).toBe('13:00')
})

/**
 * A date with no windows is blocked, which is a stronger statement than the
 * closed day the same gesture makes in the week pane — so this button, which
 * promises hours, refuses rather than making it. Blocking is its own pick on
 * the control above, not something this button can be talked into doing.
 */
test("emptying a date's windows refuses to save rather than blocking the date", () => {
  const morning = { date: A_SUNDAY, windows: [{ startMinute: NINE_AM, endMinute: NOON }] }

  render(<PatternPreview pattern={usedByMentorship([morning])} />)

  fireEvent.click(screen.getByRole('button', { name: 'Show Sunday 23 August' }))
  fireEvent.click(screen.getByRole('button', { name: 'Remove Sunday 23 August window 1' }))

  const save = screen.getByRole('button', { name: 'Save this date' })
  expect(save.getAttribute('aria-disabled')).toBe('true')
  expect(screen.getByText(/A date needs a window/)).toBeDefined()

  fireEvent.click(save)

  expect(savePatternOverride).not.toHaveBeenCalled()
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
        { date: A_TUESDAY, windows: [] },
        { date: A_SUNDAY, windows: [{ startMinute: NINE_AM, endMinute: ONE_PM }] },
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

/**
 * Finding 10's remainder. The three radios have been inert while a write is in
 * flight since the three-state control landed; this button was not, so a second
 * press was taken by the UI and then dropped by `write`'s own guard — the one
 * failure mode where the operator has no way to tell a refusal from a slow
 * network.
 */
test('Save this date is inert while a write is in flight', async () => {
  const { promise, resolve } = deferred<Pattern>()
  vi.mocked(savePatternOverride).mockReturnValue(promise)

  render(<PatternPreview pattern={usedByMentorship([OWN_HOURS])} />)
  fireEvent.click(day(A_SUNDAY_LABEL))

  const save = screen.getByRole('button', { name: 'Save this date' })
  fireEvent.click(save)
  expect(savePatternOverride).toHaveBeenCalledTimes(1)

  expect(save.getAttribute('aria-disabled')).toBe('true')
  fireEvent.click(save)
  expect(savePatternOverride).toHaveBeenCalledTimes(1)

  resolve(usedByMentorship([OWN_HOURS]))
  await waitFor(() =>
    expect(
      screen.getByRole('button', { name: 'Save this date' }).getAttribute('aria-disabled'),
    ).toBeNull(),
  )
})

/**
 * Finding 11. The effect returned early when the last link stopped standing on
 * the pattern, leaving `links` holding whatever it held — so the picker went on
 * offering a link that had moved, and `chosen` went on resolving, so every month
 * change fetched that link's slots.
 */
test('the times picker goes when the last link standing on the pattern does', async () => {
  const { rerender } = render(<PatternPreview pattern={usedByMentorship()} />)

  await waitFor(() => expect(screen.getByLabelText('Show')).toBeTruthy())

  rerender(<PatternPreview pattern={{ ...weekdays, usedBy: [] }} />)

  await waitFor(() => expect(screen.queryByLabelText('Show')).toBeNull())
  expect(vi.mocked(getSlots).mock.calls.length).toBe(0)
})

/**
 * The other half of finding 11, and the half clearing the links alone cannot
 * show. With `links` emptied `chosen` resolves to nothing whether or not `slug`
 * was reset, so nothing on screen tells the two apart — until the pattern gains
 * a link again and the picker comes back open on a selection that no longer
 * exists, fetching the slots of a link the operator never picked twice.
 */
test('a pattern that gains a link again opens the picker on Days only', async () => {
  vi.mocked(getSlots).mockResolvedValue([])

  const { rerender } = render(<PatternPreview pattern={usedByMentorship()} />)

  fireEvent.change(await screen.findByLabelText('Show'), { target: { value: mentorship.slug } })
  expect((screen.getByLabelText('Show') as HTMLSelectElement).value).toBe(mentorship.slug)

  rerender(<PatternPreview pattern={{ ...weekdays, usedBy: [] }} />)
  await waitFor(() => expect(screen.queryByLabelText('Show')).toBeNull())

  // Everything read while the link was genuinely selected is behind us. What
  // matters from here is whether anything is read for it after it has gone.
  vi.mocked(getSlots).mockClear()

  rerender(<PatternPreview pattern={usedByMentorship()} />)

  expect(((await screen.findByLabelText('Show')) as HTMLSelectElement).value).toBe(DAYS_ONLY)
  expect(getSlots).not.toHaveBeenCalled()
})
