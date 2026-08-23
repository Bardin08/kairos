/**
 * The four limits in words, in one place.
 *
 * Two screens describe the same four settings, and before this module they
 * described them differently: `Bookings a day` in one, `Booking cap` in the
 * other, for the same column. `short` exists because the affix inside a numeric
 * input has room for `min` and not `minutes`, while a screen reader should still
 * hear the whole word.
 */
import type { GlobalLimits } from './adminLinks'

/** The four, taken from the API's own shape so this file cannot drift from it. */
export type LimitKey = keyof GlobalLimits

/**
 * `label`, `unit`, `short` and `help` are words; `floor` is not — it rides along
 * because it is per-limit like the words are, and a fifth parallel record keyed
 * by `LimitKey` would cost more than the naming mismatch does.
 *
 * `floor` is not decoration: a negative margin inverts the padded busy interval
 * and starts creating overlaps that look like availability, and a zero start
 * increment makes the slot generator's minute step never advance.
 */
export type LimitCopy = {
  label: string
  unit: string
  short: string
  floor: number
  help: string
}

/** No gap, book immediately and nothing bookable today are all real settings. */
export const ZERO_FLOOR = 0

/** Zero here is not a smaller step; it is the step that never advances. */
const INCREMENT_FLOOR = 1

/** Reading order on both screens. Not alphabetical, not the column order. */
export const LIMIT_KEYS = [
  'marginMinutes',
  'minNoticeMinutes',
  'dailyCap',
  'startIncrementMinutes',
] as const satisfies readonly LimitKey[]

/**
 * `LIMIT_COPY` is exhaustive because `Record` says so; this array is not, and it
 * is the one both screens iterate. Without this line a limit added to the record
 * and forgotten here would silently stop rendering.
 */
type EveryKeyListed = [LimitKey] extends [(typeof LIMIT_KEYS)[number]] ? true : never
const _everyKeyListed: EveryKeyListed = true
void _everyKeyListed

export const LIMIT_COPY: Record<LimitKey, LimitCopy> = {
  marginMinutes: {
    label: 'Margin between meetings',
    unit: 'minutes',
    short: 'min',
    floor: ZERO_FLOOR,
    help: 'Kept clear on both sides of every booking.',
  },
  minNoticeMinutes: {
    label: 'Minimum notice',
    unit: 'minutes',
    short: 'min',
    floor: ZERO_FLOOR,
    help: 'How far ahead the earliest bookable slot sits. Zero is bookable immediately.',
  },
  dailyCap: {
    label: 'Booking cap',
    unit: 'a day',
    // `short` matches `unit` here, unlike the three minute-valued limits: the
    // spoken form and the input affix are the same three characters, not a
    // truncation of them, so there is nothing left to shorten.
    short: 'a day',
    floor: ZERO_FLOOR,
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
