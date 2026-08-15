/**
 * The four limits in words, in one place.
 *
 * Two screens describe the same four settings — the globals in Settings and
 * the per-link overrides in the link editor — and before this module they
 * described them differently: `Bookings a day` in one and `Booking cap` in
 * the other, for the same column. A reader cannot tell whether that is two
 * settings or one.
 *
 * `short` exists because the affix inside a numeric input has room for `min`
 * and not for `minutes`, while a screen reader should still hear the whole
 * word from the label. `floor` is not decoration: a negative margin inverts
 * the padded busy interval and starts creating overlaps that look like
 * availability, and a zero start increment makes the slot generator's minute
 * step never advance.
 */
export type LimitKey =
  | 'marginMinutes'
  | 'minNoticeMinutes'
  | 'dailyCap'
  | 'startIncrementMinutes'

export type LimitCopy = {
  label: string
  unit: string
  short: string
  floor: number
  help: string
}

/** No gap, book immediately and nothing bookable today are all real settings. */
const NO_FLOOR = 0

/** Zero here is not a smaller step; it is the step that never advances. */
const INCREMENT_FLOOR = 1

/** Reading order on both screens. Not alphabetical, not the column order. */
export const LIMIT_KEYS: readonly LimitKey[] = [
  'marginMinutes',
  'minNoticeMinutes',
  'dailyCap',
  'startIncrementMinutes',
]

export const LIMIT_COPY: Record<LimitKey, LimitCopy> = {
  marginMinutes: {
    label: 'Margin between meetings',
    unit: 'minutes',
    short: 'min',
    floor: NO_FLOOR,
    help: 'Kept clear on both sides of every booking.',
  },
  minNoticeMinutes: {
    label: 'Minimum notice',
    unit: 'minutes',
    short: 'min',
    floor: NO_FLOOR,
    help: 'How far ahead the earliest bookable slot sits. Zero is bookable immediately.',
  },
  dailyCap: {
    label: 'Booking cap',
    unit: 'a day',
    short: 'a day',
    floor: NO_FLOOR,
    help: 'The most meetings one day may take. Zero closes every day.',
  },
  startIncrementMinutes: {
    label: 'Start increment',
    unit: 'minutes',
    short: 'min',
    floor: INCREMENT_FLOOR,
    help: 'The grid starts land on — 30 gives you :00 and :30.',
  },
}
