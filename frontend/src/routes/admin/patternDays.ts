// A month of days as the pattern alone shapes them: open, closed, blocked, past
// — and the hours a date would open on if it were given its own.
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
import { clock } from './adminTime'
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
 *
 * `today` is the ORGANIZER's date too, because the pattern's hours are their
 * wall clock. Every day before it is `past` whatever the week or an override
 * says, and that outranks the rest: blocking a day that has already happened
 * changes nothing an invitee can be offered, so the one thing left worth
 * reporting about the square is that it is not a control. What an override on
 * such a date said stays legible in the exceptions list, which is where a date
 * with its own hours has always been read.
 */
export function patternDays({
  month,
  rules,
  overrides,
  today,
}: {
  month: string
  rules: readonly PatternRule[]
  overrides: readonly PatternOverride[]
  today: string
}): Map<string, DayState> {
  const exceptions = new Map(overrides.map((override) => [override.date, override]))
  const openWeekdays = new Set(rules.map((rule) => rule.isoDayOfWeek))
  const states = new Map<string, DayState>()

  for (const day of monthCells(month)) {
    // The squares before the first of the month, which belong to the month
    // before it.
    if (day === null) continue

    // Date keys sort lexicographically, so a string compare is the whole
    // comparison — and it holds across months, which is what makes every square
    // of a month before this one read as past. Today itself is not: bookings
    // later today are still ahead.
    if (day < today) {
      states.set(day, 'past')
      continue
    }

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

/** Two empty fields: nothing typed yet, which is not the same as 00:00. */
export const NO_HOURS = { start: '', end: '' }

/**
 * The window a date's own pair of time fields should open on, in the `HH:MM` an
 * `<input type="time">` speaks.
 *
 * What the date would otherwise offer, so that moving one end leaves the other
 * already sensible: its own hours where it has them, and otherwise the weekly
 * window its weekday opens with. Both ends together, because the endpoint takes
 * both or neither — a single one is refused as `override_incomplete` — so a
 * partial override is not a thing this pane can express, and the inconvenience
 * the operator hit is a defaulting problem rather than a validation one.
 *
 * Blank on a weekday the pattern never opens, which is the one case with
 * nothing to borrow. A nine-to-five invented here would be hours nobody chose
 * to offer on a day nobody chose to work, which is the same reason WeekEditor
 * opens a new window blank.
 *
 * The earliest window seeds a split day: it is the one the day opens with, and
 * it is the order WeekEditor already lists a day's windows in. A date can hold
 * only one window, so the later ones cannot be reproduced on it at all — that
 * limitation is not this function's to fix.
 */
export function hoursOn(
  day: string,
  rules: readonly PatternRule[],
  override?: PatternOverride,
): { start: string; end: string } {
  if (override !== undefined && override.startMinute !== null && override.endMinute !== null) {
    return { start: clock(override.startMinute), end: clock(override.endMinute) }
  }

  // Filtered first, so the sort is on a copy and the caller's rules keep their
  // order.
  const opening = rules
    .filter((rule) => rule.isoDayOfWeek === isoDayOfWeek(day))
    .sort((left, right) => left.startMinute - right.startMinute)[0]

  return opening === undefined
    ? NO_HOURS
    : { start: clock(opening.startMinute), end: clock(opening.endMinute) }
}
