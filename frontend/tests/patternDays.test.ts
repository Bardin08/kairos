import { expect, test } from 'vitest'

import { hoursOn, patternDays } from '../src/routes/admin/patternDays'
import type { PatternRule } from '../src/routes/admin/patterns'

// Minutes from local midnight.
const MINUTES_PER_HOUR = 60
const NINE_AM = 9 * MINUTES_PER_HOUR
const NOON = 12 * MINUTES_PER_HOUR
const ONE_PM = 13 * MINUTES_PER_HOUR
const FIVE_PM = 17 * MINUTES_PER_HOUR

// ISO-8601: 1 is Monday, 7 is Sunday.
const TUESDAY = 2

// August 2026. The 18th and the 25th are Tuesdays, the 19th a Wednesday, and
// the 23rd a Sunday.
const MONTH = '2026-08'
const DAYS_IN_AUGUST = 31

// The organizer's own date, which is what decides who is past. Before the 18th,
// so the Tuesday the rest of this file uses is still ahead.
const TODAY = '2026-08-15'

const onTuesdays: PatternRule[] = [
  { id: 'r1', isoDayOfWeek: TUESDAY, startMinute: NINE_AM, endMinute: FIVE_PM },
]

/**
 * Breaks if a blocked date is read as a day with no hours. The two are one
 * value in a count — both are zero — and telling them apart is the whole reason
 * the editor previews a pattern as a month rather than as a number.
 */
test('reads a blocked date as blocked, not as a day with no hours', () => {
  const states = patternDays({
    month: MONTH,
    rules: onTuesdays,
    overrides: [{ date: '2026-08-18', startMinute: null, endMinute: null }],
    today: TODAY,
  })

  // 18 Aug 2026 is a Tuesday, so the weekly rule would have opened it.
  expect(states.get('2026-08-18')).toBe('blocked')
  expect(states.get('2026-08-25')).toBe('open')
  expect(states.get('2026-08-19')).toBe('closed')
})

/**
 * Breaks if a day is left out of the map. `MonthGrid` renders no attribute for
 * a day it has no state for, which is byte for byte what it renders when no
 * states are passed at all — so a forgotten day does not draw as wrong, it
 * draws as the booking page's default and says nothing about the pattern.
 */
test('supplies every day of the month, including the closed ones', () => {
  const states = patternDays({ month: MONTH, rules: onTuesdays, overrides: [], today: TODAY })

  expect(states.size).toBe(DAYS_IN_AUGUST)
  // Both Sundays, and both still ahead of TODAY — a day the week never opens
  // and a day already gone are different answers, and this test asks for the
  // first one.
  expect(states.get('2026-08-16')).toBe('closed')
  expect(states.get('2026-08-30')).toBe('closed')
})

/**
 * The defect: a date that has already happened was as clickable as any other,
 * so a pattern collected blocks that could not change what anyone was offered.
 * Past outranks everything the week or an override says, and today is not past
 * — bookings later today are still ahead, and closing today would take a
 * working day away for nothing.
 */
test('a date before today is past, and today itself is not', () => {
  const states = patternDays({
    month: MONTH,
    rules: onTuesdays,
    // 11 August is a Tuesday the week opens and 6 August a blocked Thursday:
    // the two readings that must both give way to the date being gone.
    overrides: [{ date: '2026-08-06', startMinute: null, endMinute: null }],
    today: TODAY,
  })

  expect(states.get('2026-08-11')).toBe('past')
  expect(states.get('2026-08-06')).toBe('past')
  expect(states.get(TODAY)).toBe('closed')
})

/**
 * The month arrows reach backwards, so a whole month can be behind today.
 * Breaks if past is worked out within the month being drawn rather than against
 * the date key entire.
 */
test('every day of a month before this one is past', () => {
  const states = patternDays({
    month: '2026-07',
    rules: onTuesdays,
    overrides: [],
    today: TODAY,
  })

  expect([...states.values()].every((state) => state === 'past')).toBe(true)
})

/**
 * The second defect: the pair of time fields opened on whatever an unset
 * `<input type="time">` shows, so the operator had to set both ends before the
 * date could be saved at all — even to move one of them by half an hour. They
 * open on what the date would otherwise offer instead.
 */
test('a date borrows the hours its weekday already opens on', () => {
  expect(hoursOn('2026-08-18', onTuesdays)).toEqual({ start: '09:00', end: '17:00' })
})

/**
 * A weekday the pattern never opens has nothing to borrow, and hours invented
 * here would be hours nobody chose to offer on a day nobody chose to work.
 */
test('a date on a closed weekday borrows nothing', () => {
  expect(hoursOn('2026-08-23', onTuesdays)).toEqual({ start: '', end: '' })
})

/**
 * A date that already states its own hours opens on those and not on the
 * week's, or reading an exception would propose replacing it with something
 * else. A blocked date states none, so it falls back to the week — which is
 * what unblocking it would give it.
 */
test('a date with its own hours opens on them, and a blocked one on the week', () => {
  const day = '2026-08-18'

  expect(
    hoursOn(day, onTuesdays, { date: day, startMinute: NINE_AM, endMinute: ONE_PM }),
  ).toEqual({ start: '09:00', end: '13:00' })
  expect(hoursOn(day, onTuesdays, { date: day, startMinute: null, endMinute: null })).toEqual({
    start: '09:00',
    end: '17:00',
  })
})

/**
 * A split day has two windows and a date can hold one, so the earliest is what
 * seeds the fields — the window the day opens on, and the first one WeekEditor
 * lists. Breaks if the rules' own order is trusted, which is the server's and
 * not the clock's.
 */
test('a split weekday seeds the fields from its earliest window', () => {
  const split: PatternRule[] = [
    { id: 'r2', isoDayOfWeek: TUESDAY, startMinute: ONE_PM, endMinute: FIVE_PM },
    { id: 'r1', isoDayOfWeek: TUESDAY, startMinute: NINE_AM, endMinute: NOON },
  ]

  expect(hoursOn('2026-08-18', split)).toEqual({ start: '09:00', end: '12:00' })
})

/**
 * An override with hours in it replaces the day, and replacing a closed day
 * opens it. Breaks if overrides are treated as a subtraction from the week —
 * the reading that makes a one-off Sunday clinic invisible in the month that
 * has it.
 */
test('a date given its own hours is open even where the week is closed', () => {
  const states = patternDays({
    month: MONTH,
    rules: onTuesdays,
    overrides: [{ date: '2026-08-23', startMinute: NINE_AM, endMinute: ONE_PM }],
    today: TODAY,
  })

  expect(states.get('2026-08-23')).toBe('open')
})
