import type { CSSProperties, Ref } from 'react'

import { dayOffset, longDate, zonedTime } from '../lib/time'

// U+2212 MINUS SIGN, not a hyphen: it is the same width as the plus in a
// tabular face, so a column carrying both does not step in and out.
const DAY_MARK = { after: ' +1d', before: ' −1d' }

function shift(offset: number): string {
  if (offset === 0) return ''
  return offset > 0 ? DAY_MARK.after : DAY_MARK.before
}

function counted(total: number): string {
  return total === 1 ? '1 time' : `${total} times`
}

/**
 * The times on one day, in the invitee's zone.
 *
 * The day heading is the organizer's day, because that is the day the slots
 * were asked for. The times under it are the invitee's, so one of them can
 * legitimately read 00:30 on a Monday heading — hence the `+1d` mark, which is
 * the difference between an invitee arriving on the right day and the wrong one.
 *
 * `resetKey` is what makes the one authored motion moment in Kairos fire.
 * Changing it remounts the list, which restarts the stagger; a selection leaves
 * it alone, so picking a time does not re-animate the column under the finger
 * that picked it.
 */
export default function SlotList({
  day,
  slots,
  selected,
  zone,
  animate,
  resetKey,
  onSelect,
  ref,
}: {
  /** Date key in the ORGANIZER's zone. */
  day: string
  /** Verbatim instants from the API, in order. */
  slots: string[]
  selected: string | null
  /** The INVITEE's zone. Every time below is rendered in it. */
  zone: string
  animate: boolean
  resetKey: string
  onSelect: (start: string) => void
  ref: Ref<HTMLElement>
}) {
  return (
    // Focus lands here when the day changes: on a server-rendered page the
    // navigation would have done it, and on an SPA nothing does unless it is
    // written. tabIndex -1 keeps it out of the tab order but reachable.
    <section ref={ref} tabIndex={-1} aria-label={`Times on ${longDate(day)}`}>
      <div className="slots-head">
        <h3 className="slots-date numeric">{longDate(day)}</h3>
        {/* Polite by role. The count is the one thing a sighted reader gets
            for free from the column changing shape and nobody else does. */}
        <span className="fine numeric" role="status">
          {counted(slots.length)}
        </span>
      </div>

      <div
        className="slots"
        data-animate={animate}
        role="group"
        aria-label="Available times"
        key={resetKey}
      >
        {slots.map((start, index) => (
          // The stagger's per-item delay, and the only inline style in Kairos. It sets a custom
          // property the stylesheet consumes; the rule matches on the prop name and cannot tell.
          // eslint-disable-next-line react/forbid-dom-props -- DESIGN.md stagger: --i is per-item
          <button style={{ '--i': index } as CSSProperties}
            key={start}
            type="button"
            className="slot"
            aria-pressed={start === selected}
            onClick={() => onSelect(start)}
          >
            {zonedTime(start, zone)}
            {shift(dayOffset(start, zone, day))}
          </button>
        ))}
      </div>
    </section>
  )
}
