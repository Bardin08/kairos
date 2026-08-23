import { longDate, monthCells, monthLabel } from '../lib/time'

// Monday-first, matching `time.ts`'s cell order.
const WEEKDAYS = ['Mo', 'Tu', 'We', 'Th', 'Fr', 'Sa', 'Su']

const DAY_OF_MONTH_AT = 'YYYY-MM-'.length

function availability(count: number): string {
  if (count === 0) return 'no times available'
  return count === 1 ? '1 time available' : `${count} times available`
}

/**
 * What a day is, when the caller knows. Absent on the booking page, which only
 * has counts. `past` is the one value that is not about the pattern: it says the
 * day has already happened, and no square that carries it can be pressed.
 */
export type DayState = 'open' | 'closed' | 'blocked' | 'past'

/**
 * A month of days, keyed on the ORGANIZER's calendar — which is what the slots
 * endpoint's `from` and `to` mean.
 *
 * Days with nothing in them stay in place and dimmed rather than vanishing. An
 * invitee needs to see that a month is mostly full; a calendar with holes
 * punched in it reads as a rendering fault, not as a busy fortnight.
 */
export default function MonthGrid({
  month,
  counts,
  states,
  selected,
  onSelect,
}: {
  month: string
  /**
   * Slots per date key. A day missing from the map has none — and the whole map
   * missing means times are not known at all, which is not the same thing: the
   * pattern editor's default preview is days alone, and it has no link to count
   * the times of.
   */
  counts?: Map<string, number>
  /**
   * Why a day is what it is, per date key. A count cannot say it: a blocked day
   * and a day the pattern never opens both count zero, and the pattern editor
   * has to tell them apart.
   */
  states?: Map<string, DayState>
  selected: string | null
  onSelect: (day: string) => void
}) {
  return (
    <div>
      <div className="weekdays" aria-hidden="true">
        {WEEKDAYS.map((weekday) => (
          <span key={weekday}>{weekday}</span>
        ))}
      </div>

      <div className="days" role="group" aria-label={monthLabel(month)}>
        {monthCells(month).map((day, index) => {
          if (day === null) {
            // The squares before the first of the month, and the padding after
            // the last that keeps every month six rows tall (see `monthCells`).
            // Rendered rather than skipped, or the columns stop lining up under
            // their headings. Keyed on the index alone: neither group has a date
            // key, and the index is already unique across the array.
            return (
              <span
                className="day"
                data-state="empty"
                key={index}
                aria-hidden="true"
              />
            )
          }

          const count = counts?.get(day) ?? 0
          const state = states?.get(day)
          // "There is something here", asked of whichever the caller knows:
          // times, when it has them, and otherwise the pattern opening the day.
          // Not both — an open day the link offers no times on must read as the
          // empty day it is, which is the answer the editor is checking for.
          const open = counts === undefined ? state === 'open' : count > 0
          // Every square is live in the pattern editor: blocking a Sunday the
          // pattern never opens is legitimate, and there is no count on that day
          // to say so. A day already past is the exception, and it is the
          // caller's word — the day it has passed is the organizer's, not this
          // browser's.
          const actionable = (open || state !== undefined) && state !== 'past'

          return (
            <button
              key={day}
              type="button"
              className="day"
              data-state={open ? 'available' : 'unavailable'}
              // Undefined renders no attribute at all, which is what keeps the
              // booking page's markup identical to what it was before states.
              data-day-state={state}
              // Only a day that can be chosen carries a pressed state; setting
              // it on the rest would paint every closed day as a selection the
              // moment the stylesheet's `[aria-pressed]` rule matched.
              aria-pressed={actionable ? day === selected : undefined}
              disabled={!actionable}
              // Each part only where the caller could know it, so the booking
              // page still announces a date and a count and nothing else.
              aria-label={[longDate(day), state, counts && availability(count)]
                .filter(Boolean)
                .join(', ')}
              onClick={() => onSelect(day)}
            >
              {Number(day.slice(DAY_OF_MONTH_AT))}
            </button>
          )
        })}
      </div>
    </div>
  )
}
