// The calendar helpers only the workbench uses.
//
// Here rather than in lib/time.ts for the same measured reason adminMeetings.ts
// exists beside it: lib/time.ts is imported by the booking page, so a helper
// left in it ships in the entry chunk even when nothing an invitee can reach
// calls it — Rollup shakes out an export nobody imports, but not one a lazy
// chunk imports, because the module it lives in is already in the eager graph.
// Every one of these is the workbench's alone. Anything the booking page also
// needs belongs back in lib/time.ts, whose header explains the two vocabularies
// these follow: an INSTANT is what the API speaks, a DATE KEY is a civil date
// with no zone of its own.
//
// A third vocabulary starts here and nowhere else: a MINUTE OF DAY, which is
// what an availability rule holds — minutes from local midnight in the
// organizer's zone, with no date attached and therefore no instant to derive.

const LOCALE = 'en-GB'
const START_OF_DAY = 'T00:00:00Z'
const DATE_KEY_LENGTH = 'YYYY-MM-DD'.length
const MINUTES_PER_HOUR = 60
const LAST_HOUR = 23
const CLOCK_DIGITS = 'HH'.length

// Construction is the expensive part of `Intl.DateTimeFormat`, not formatting,
// and a month of meetings asks for one chip per row. Built once, like the cache
// lib/time.ts keeps for the same reason — one shape here rather than a map.
let months: Intl.DateTimeFormat | undefined

/** `2026-09-07` → `Sep`. The month as a record's date chip prints it. */
export function shortMonth(day: string): string {
  months ??= new Intl.DateTimeFormat(LOCALE, { month: 'short', timeZone: 'UTC' })
  return months.format(new Date(day + START_OF_DAY))
}

/**
 * The date key `delta` days from this one. Negative goes back.
 *
 * Days rather than milliseconds: `setUTCDate` rolls the month and the year on
 * its own, and the key it produces is a civil date with no zone, so a clock
 * that shifted an hour in between cannot move it.
 */
export function shiftDays(day: string, delta: number): string {
  const date = new Date(day + START_OF_DAY)
  date.setUTCDate(date.getUTCDate() + delta)
  return date.toISOString().slice(0, DATE_KEY_LENGTH)
}

/** `540` → `09:00`. Zero-padded, so a column of them keeps one width. */
export function clock(minuteOfDay: number): string {
  const hour = Math.floor(minuteOfDay / MINUTES_PER_HOUR)
  const minute = minuteOfDay % MINUTES_PER_HOUR
  return `${String(hour).padStart(CLOCK_DIGITS, '0')}:${String(minute).padStart(CLOCK_DIGITS, '0')}`
}

/**
 * `09:00` → `540`, and null for anything else.
 *
 * `<input type="time">` answers with an empty string until it is filled in, so
 * null is "not stated yet" — which is what keeps a half-typed window out of the
 * request rather than out of the field.
 */
export function minuteOfDay(value: string): number | null {
  const match = /^(\d{2}):(\d{2})$/.exec(value)
  if (match === null) return null

  const hour = Number(match[1])
  const minute = Number(match[2])
  if (hour > LAST_HOUR || minute >= MINUTES_PER_HOUR) return null

  return hour * MINUTES_PER_HOUR + minute
}
