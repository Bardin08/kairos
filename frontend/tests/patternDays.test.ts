import { expect, test } from 'vitest'

import { patternDays } from '../src/routes/admin/patternDays'
import type { PatternRule } from '../src/routes/admin/patterns'

// Minutes from local midnight.
const MINUTES_PER_HOUR = 60
const NINE_AM = 9 * MINUTES_PER_HOUR
const ONE_PM = 13 * MINUTES_PER_HOUR
const FIVE_PM = 17 * MINUTES_PER_HOUR

// ISO-8601: 1 is Monday, 7 is Sunday.
const TUESDAY = 2

// August 2026. The 18th and the 25th are Tuesdays, the 19th a Wednesday, and
// the 23rd a Sunday.
const MONTH = '2026-08'
const DAYS_IN_AUGUST = 31

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
  const states = patternDays({ month: MONTH, rules: onTuesdays, overrides: [] })

  expect(states.size).toBe(DAYS_IN_AUGUST)
  expect(states.get('2026-08-01')).toBe('closed')
  expect(states.get('2026-08-31')).toBe('closed')
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
  })

  expect(states.get('2026-08-23')).toBe('open')
})
