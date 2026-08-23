// Every calendar calculation the booking and manage pages need, on `Intl` and
// `Date` alone.
//
// Helpers only the workbench uses are NOT here, for the same reason its HTTP
// calls are not in lib/api.ts: this module is in the booking route's chunk, and
// Rollup shakes out an export nobody imports but keeps one a lazy chunk imports.
// See routes/admin/adminTime.ts.
//
// A date library would be the obvious reach and it is the one thing the budget
// forbids: the shell already spends 61,641 of 76,800 gzipped bytes, and the
// smallest useful date library is larger than everything Kairos writes. `Intl`
// costs nothing and carries the whole tzdb — which is what matters here, because
// the organizer's zone and the invitee's zone are different zones and neither is
// the machine's offset.
//
// Two vocabularies, and confusing them is the bug this file exists to prevent:
//
//   an INSTANT is what the API speaks — "2026-09-07T09:00:00Z", verbatim, and
//   it goes back to the server byte for byte because an offset form is refused.
//
//   a DATE KEY is what a calendar square is — "2026-09-07", a civil date with
//   no time and therefore no zone of its own. Which zone produced it is the
//   caller's business: the month grid keys on the ORGANIZER's date, because
//   that is what `from`/`to` mean to the slots endpoint, while the times inside
//   a day render in the INVITEE's zone and may fall on the day before or after.

// Formatting is fixed to en-GB rather than the visitor's locale: a 24-hour
// clock keeps a column of times one width, which is the whole point of the
// tabular figures, and every other word on the page is English anyway.
const LOCALE = 'en-GB'

const MS_PER_MINUTE = 60_000
const DAYS_PER_WEEK = 7
// Monday-first weeks. Date.getUTCDay() is Sunday-first, so Sunday's 0 has to
// become 6 and Monday's 1 has to become 0.
const SUNDAY_TO_MONDAY_FIRST = DAYS_PER_WEEK - 1
const MONTHS_PER_YEAR = 12
// A month grid is always this many cells, whether the month itself needs five
// rows or six. Padding the short month out to the long month's size is what
// `monthCells` uses these for — see its comment.
const GRID_COLUMNS = DAYS_PER_WEEK
const GRID_ROWS = 6
const GRID_CELLS = GRID_COLUMNS * GRID_ROWS
// A date key is unambiguous at midnight UTC as long as it is only ever read
// back in UTC, which is what every helper below does.
const START_OF_DAY = 'T00:00:00Z'
const DATE_KEY_LENGTH = 'YYYY-MM-DD'.length
const MONTH_KEY_LENGTH = 'YYYY-MM'.length

// `Intl.DateTimeFormat` construction is the expensive part, not formatting, and
// grouping a month of slots by day builds one key per instant — several hundred
// on a busy link. Built once per zone and shape, then reused.
const formatters = new Map<string, Intl.DateTimeFormat>()

function formatter(
  shape: string,
  zone: string,
  options: Intl.DateTimeFormatOptions,
): Intl.DateTimeFormat {
  const id = `${shape}|${zone}`
  const cached = formatters.get(id)
  if (cached) return cached

  const built = new Intl.DateTimeFormat(LOCALE, { ...options, timeZone: zone })
  formatters.set(id, built)
  return built
}

// `formatToParts` rather than a format string: it is the only way to read the
// numbers back out without depending on how a locale happens to punctuate them.
function part(
  parts: Intl.DateTimeFormatPart[],
  type: Intl.DateTimeFormatPartTypes,
): string {
  return parts.find((entry) => entry.type === type)?.value ?? ''
}

/** The zone this browser is in. The one the invitee reads every time in. */
export function localZone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone
}

/** The date key an instant falls on, in the given zone. */
export function zonedDate(instant: string, zone: string): string {
  const parts = formatter('date', zone, {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).formatToParts(new Date(instant))

  return `${part(parts, 'year')}-${part(parts, 'month')}-${part(parts, 'day')}`
}

/** The clock time an instant reads as, in the given zone. `09:00`. */
export function zonedTime(instant: string, zone: string): string {
  return formatter('time', zone, {
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  }).format(new Date(instant))
}

/**
 * The zone's short offset at an instant — `GMT+2`, `GMT-4`.
 *
 * It exists for one hour a year. When a zone falls back, two instants an hour
 * apart render the same clock time: in Warsaw on 2026-10-25 both 00:30Z and
 * 01:30Z are "02:30". The booking stays correct either way — the instant is
 * echoed to the server verbatim — but the invitee is shown two identical buttons
 * and cannot tell which hour they are choosing. The offset separates them.
 */
export function zonedOffset(instant: string, zone: string): string {
  const parts = formatter('offset', zone, {
    hour: '2-digit',
    timeZoneName: 'shortOffset',
  }).formatToParts(new Date(instant))

  return part(parts, 'timeZoneName')
}

/**
 * How far the invitee's day is from the organizer's. A 17:00 Warsaw slot is
 * 00:00 the next day in Tokyo, and a slot list that renders it as plain 00:00
 * under a Monday heading has told the invitee the wrong day. Date keys sort
 * lexicographically, so a string compare is the whole comparison.
 */
export function dayOffset(instant: string, zone: string, day: string): number {
  const shown = zonedDate(instant, zone)
  if (shown === day) return 0
  return shown > day ? 1 : -1
}

/** `2026-09-07` → `Monday 7 September`. */
export function longDate(day: string): string {
  return formatter('long', 'UTC', {
    weekday: 'long',
    day: 'numeric',
    month: 'long',
  }).format(new Date(day + START_OF_DAY))
}

/** `2026-09-07` → `Mon 7 Sep`. The action bar's width is a phone's width. */
export function shortDate(day: string): string {
  return formatter('short', 'UTC', {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
  }).format(new Date(day + START_OF_DAY))
}

/** `2026-09-07` → `September 2026`. */
export function monthLabel(day: string): string {
  return formatter('month', 'UTC', { month: 'long', year: 'numeric' }).format(
    new Date(day + START_OF_DAY),
  )
}

/** The instant a meeting of this length starting here would end. */
export function endOf(instant: string, minutes: number): string {
  return new Date(Date.parse(instant) + minutes * MS_PER_MINUTE).toISOString()
}

/** Today's date key in the given zone. */
export function todayIn(zone: string): string {
  return zonedDate(new Date().toISOString(), zone)
}

/** The first day of the month a date key belongs to. */
export function startOfMonth(day: string): string {
  return `${day.slice(0, MONTH_KEY_LENGTH)}-01`
}

/** The first day of the month `delta` months from this one. */
export function shiftMonth(day: string, delta: number): string {
  const date = new Date(startOfMonth(day) + START_OF_DAY)
  date.setUTCMonth(date.getUTCMonth() + delta)
  return date.toISOString().slice(0, DATE_KEY_LENGTH)
}

/**
 * A month as the grid draws it: leading nulls for the squares before the first,
 * then a date key per day, then trailing nulls out to a fixed six rows. The
 * nulls are rendered rather than skipped so the day columns line up under their
 * weekday headings, and so the calendar lines up with itself month to month — a
 * five-row February beside a six-row August is a booking card that changes
 * height when the invitee changes month.
 *
 * The first and last real-day keys are also the `from` and `to` of the slots
 * request. A 31-day month is exactly the 31 days that endpoint answers for, so a
 * month is the largest window it will ever be asked for; the trailing nulls are
 * not part of that window, only of the grid drawn around it.
 */
export function monthCells(month: string): (string | null)[] {
  const first = new Date(startOfMonth(month) + START_OF_DAY)
  const year = first.getUTCFullYear()
  const index = first.getUTCMonth()
  // Day 0 of the next month is the last day of this one.
  const length = new Date(Date.UTC(year, index + 1, 0)).getUTCDate()

  const blanks = (first.getUTCDay() + SUNDAY_TO_MONDAY_FIRST) % DAYS_PER_WEEK
  const cells: (string | null)[] = new Array<null>(blanks).fill(null)

  for (let day = 1; day <= length; day++) {
    cells.push(
      new Date(Date.UTC(year, index, day)).toISOString().slice(0, DATE_KEY_LENGTH),
    )
  }

  while (cells.length < GRID_CELLS) cells.push(null)

  return cells
}

/** Ordinal months, so two months can be compared without parsing them twice. */
export function monthIndex(day: string): number {
  const date = new Date(startOfMonth(day) + START_OF_DAY)
  return date.getUTCFullYear() * MONTHS_PER_YEAR + date.getUTCMonth()
}
