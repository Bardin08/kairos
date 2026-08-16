// A month of days as the pattern alone shapes them: open, closed, blocked.
//
// It knows nothing about margin, minimum notice, daily cap or start increment,
// and that is the design rather than an omission. Those four differ per link
// while a pattern is shared, so a month drawn through them would be true of one
// link and a lie about every other; and they are the expensive half of the
// booking engine, so a second implementation here is the copy that goes stale.
// When the preview needs real bookable times it asks the slots endpoint for
// them. What is left is small enough to be obviously right.

import type { DayState } from '../../components/MonthGrid'
import { monthCells } from '../../lib/time'
import type { PatternOverride, PatternRule } from './patterns'

// A date key is unambiguous at midnight UTC as long as it is only ever read
// back in UTC, which is what `isoDayOfWeek` does. Same convention as lib/time.
const START_OF_DAY = 'T00:00:00Z'

// `Date.getUTCDay()` calls Sunday 0; ISO-8601, which is what a rule holds,
// calls it 7. Monday to Saturday agree.
const SUNDAY_AS_ZERO = 0
const SUNDAY = 7

function isoDayOfWeek(day: string): number {
  const weekday = new Date(day + START_OF_DAY).getUTCDay()
  return weekday === SUNDAY_AS_ZERO ? SUNDAY : weekday
}

/**
 * What each day of `month` is, keyed on the ORGANIZER's date — the same key the
 * slots endpoint's `from` and `to` speak, and therefore the same key a square
 * can be said to contain.
 *
 * Every day of the month is in the map, closed ones included. `MonthGrid`
 * renders no attribute for a day it was given no state for, which is byte for
 * byte what it renders when no states are passed at all, so a day left out does
 * not draw as wrong — it draws as the booking page's default.
 */
export function patternDays({
  month,
  rules,
  overrides,
}: {
  month: string
  rules: readonly PatternRule[]
  overrides: readonly PatternOverride[]
}): Map<string, DayState> {
  const exceptions = new Map(overrides.map((override) => [override.date, override]))
  const openWeekdays = new Set(rules.map((rule) => rule.isoDayOfWeek))
  const states = new Map<string, DayState>()

  for (const day of monthCells(month)) {
    // The squares before the first of the month, which belong to the month
    // before it.
    if (day === null) continue

    const exception = exceptions.get(day)
    if (exception !== undefined) {
      // A date states its own hours or states that it has none, and either way
      // it replaces the week rather than narrowing it: an override with hours
      // opens a day the rules never open.
      states.set(day, exception.startMinute === null ? 'blocked' : 'open')
      continue
    }

    states.set(day, openWeekdays.has(isoDayOfWeek(day)) ? 'open' : 'closed')
  }

  return states
}
