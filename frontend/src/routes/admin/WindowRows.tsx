import type { ReactNode } from 'react'

import { BLANK, type Draft } from './adminTime'

/**
 * One day's windows as rows — a time pair each, a Remove each, Add window
 * beneath. One component for two panes because it is one act on two keys: a
 * weekday's windows and a single date's are typed the same way and refused by
 * the same server check.
 *
 * `label` names the day and every control repeats it, because the week and the
 * selected date share a page: to a reader listing this page's controls, five
 * identical "Remove"s are a list nobody can act on.
 *
 * What an empty set means is decided by each pane — a closed day to the week, a
 * date offering nothing to the date panel.
 */
export default function WindowRows({
  label,
  windows,
  onChange,
  children,
}: {
  label: string
  /** Earliest first, which is the order both panes hold them in. */
  windows: readonly Draft[]
  /** Handed the whole set, never one row of it. */
  onChange: (next: Draft[]) => void
  /** Controls that sit beside Add window and belong to one pane only. */
  children?: ReactNode
}) {
  function setTime(position: number, side: keyof Draft, value: string) {
    onChange(windows.map((draft, at) => (at === position ? { ...draft, [side]: value } : draft)))
  }

  return (
    <div className="group">
      {windows.map((draft, position) => (
        // Keyed by position because a window has no identity until it is saved,
        // and the server's ids do not survive a set replaced entire.
        //
        // Not .wrap: .week-time already gives ground in the preview pane's
        // narrow column (kairos.css). Wrapping on top of that dropped × onto its
        // own line, reading as a stray row rather than a control.
        <div className="row gap-2" key={position}>
          <input
            className="input numeric week-time"
            type="time"
            aria-label={`${label}, window ${position + 1}, opens at`}
            value={draft.start}
            onChange={(event) => setTime(position, 'start', event.target.value)}
          />
          <span aria-hidden="true">–</span>
          <input
            className="input numeric week-time"
            type="time"
            aria-label={`${label}, window ${position + 1}, closes at`}
            value={draft.end}
            onChange={(event) => setTime(position, 'end', event.target.value)}
          />
          {/* .chip-x, not the red word: the preview pane's column is roughly
              276px, and Remove at full size does not fit beside a time pair
              without halving the inputs. The week pane loses the danger colour
              and the label for that constraint too, even though its own column
              had room — this component is one shared act, not two. */}
          <button
            className="chip-x"
            type="button"
            aria-label={`Remove ${label} window ${position + 1}`}
            onClick={() => onChange(windows.filter((_, at) => at !== position))}
          >
            ×
          </button>
        </div>
      ))}

      <div className="row gap-2 wrap">
        {/* Ghost and quiet rather than the bordered .btn it used to be — neither
            the selection nor the primary action. The leading + is not
            decoration: WeekEditor passes its own ghost in beside this one, and
            two bare ghosts read as one piece of link text. */}
        <button
          className="btn btn-ghost btn-muted"
          type="button"
          aria-label={`Add window to ${label}`}
          onClick={() => onChange([...windows, BLANK])}
        >
          + Add window
        </button>
        {children}
      </div>
    </div>
  )
}
