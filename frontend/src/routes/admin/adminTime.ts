// The two calendar helpers only the workbench uses.
//
// Here rather than in lib/time.ts for the same measured reason adminMeetings.ts
// exists beside it: lib/time.ts is imported by the booking page, so a helper
// left in it ships in the entry chunk even when nothing an invitee can reach
// calls it — Rollup shakes out an export nobody imports, but not one a lazy
// chunk imports, because the module it lives in is already in the eager graph.
// Both of these are the meetings list's alone. Anything the booking page also
// needs belongs back in lib/time.ts, whose header explains the two vocabularies
// these follow: an INSTANT is what the API speaks, a DATE KEY is a civil date
// with no zone of its own.

const LOCALE = 'en-GB'
const START_OF_DAY = 'T00:00:00Z'
const DATE_KEY_LENGTH = 'YYYY-MM-DD'.length

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
