import { longDate, monthCells, monthLabel } from '../lib/time'

// Monday-first, matching `time.ts`'s cell order.
const WEEKDAYS = ['Mo', 'Tu', 'We', 'Th', 'Fr', 'Sa', 'Su']

const DAY_OF_MONTH_AT = 'YYYY-MM-'.length

function availability(count: number): string {
  if (count === 0) return 'no times available'
  return count === 1 ? '1 time available' : `${count} times available`
}

/**
 * A month of days, keyed on the ORGANIZER's calendar — which is what the slots
 * endpoint's `from` and `to` mean, and therefore what a square can be said to
 * contain.
 *
 * Days with nothing in them stay in place and dimmed rather than vanishing. An
 * invitee needs to see that a month is mostly full; a calendar with holes
 * punched in it reads as a rendering fault, not as a busy fortnight.
 */
export default function MonthGrid({
  month,
  counts,
  selected,
  onSelect,
}: {
  month: string
  /** Slots per date key. A day missing from the map has none. */
  counts: Map<string, number>
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
            // The squares before the first of the month. Rendered rather than
            // skipped, or the columns stop lining up under their headings.
            return (
              <span
                className="day"
                data-state="empty"
                key={`lead-${index}`}
                aria-hidden="true"
              />
            )
          }

          const count = counts.get(day) ?? 0
          const open = count > 0

          return (
            <button
              key={day}
              type="button"
              className="day"
              data-state={open ? 'available' : 'unavailable'}
              // Only a day that can be chosen carries a pressed state; setting
              // it on the rest would paint every closed day as a selection the
              // moment the stylesheet's `[aria-pressed]` rule matched.
              aria-pressed={open ? day === selected : undefined}
              disabled={!open}
              aria-label={`${longDate(day)}, ${availability(count)}`}
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
