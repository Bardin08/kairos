// A month of days as the pattern alone shapes them: open, closed, blocked, past
// — and the hours a date would open on if it were given its own.
//
// It knows nothing about margin, minimum notice, daily cap or start increment,
// and that is the design. Those four differ per link while a pattern is shared,
// so a month drawn through them would be true of one link and a lie about every
// other; and they are the expensive half of the booking engine, so a second
// implementation here is the copy that goes stale. When the preview needs real
// bookable times it asks the slots endpoint.

import type { DayState } from '../../components/MonthGrid'
import { monthCells } from '../../lib/time'
import { BLANK, clock, type Draft } from './adminTime'
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
 * slots endpoint's `from` and `to` speak.
 *
 * Every day of the month is in the map, closed ones included. `MonthGrid`
 * renders no attribute for a day it was given no state for, which is byte for
 * byte what it renders when no states are passed at all, so a day left out draws
 * as the booking page's default rather than as wrong.
 *
 * `today` is the ORGANIZER's date too, because the pattern's hours are their
 * wall clock. Every day before it is `past` whatever the week or an override
 * says, and that outranks the rest: blocking a day that has already happened
 * changes nothing an invitee can be offered. What an override on such a date
 * said stays legible in the exceptions list.
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
    // The leading squares, which belong to the month before this one.
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
      // A date replaces the week rather than narrowing it: an override with
      // hours opens a day the rules never open.
      states.set(day, exception.windows.length === 0 ? 'blocked' : 'open')
      continue
    }

    states.set(day, openWeekdays.has(isoDayOfWeek(day)) ? 'open' : 'closed')
  }

  return states
}

/**
 * The windows a date's own editor should open on, earliest first.
 *
 * What the date would otherwise offer, so moving one end leaves the rest already
 * sensible: its own windows where it has them, otherwise every window its
 * weekday opens with. All of them, not the first — a date holds a morning and an
 * afternoon exactly as a weekday does, so seeding one of two would propose
 * closing the afternoon of a date nobody asked to change.
 *
 * One blank window on a weekday the pattern never opens, the one case with
 * nothing to borrow. Blank rather than an invented nine-to-five, for the reason
 * BLANK exists; one rather than none, because a panel that opened with no
 * windows would be proposing to block the date it was opened to give hours to.
 */
export function windowsOn(
  day: string,
  rules: readonly PatternRule[],
  override?: PatternOverride,
): Draft[] {
  if (override !== undefined && override.windows.length > 0) {
    return override.windows.map((window) => ({
      start: clock(window.startMinute),
      end: clock(window.endMinute),
    }))
  }

  // Filtered first, so the sort is on a copy and the caller's rules keep order.
  const opening = rules
    .filter((rule) => rule.isoDayOfWeek === isoDayOfWeek(day))
    .sort((left, right) => left.startMinute - right.startMinute)

  return opening.length === 0
    ? [BLANK]
    : opening.map((rule) => ({ start: clock(rule.startMinute), end: clock(rule.endMinute) }))
}
