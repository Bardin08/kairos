import type { ReactNode } from 'react'

import { BLANK, type Draft } from './adminTime'

/**
 * One day's windows as rows — a time pair each, a Remove each, Add window
 * beneath.
 *
 * One component for two panes because it is one act on two keys: a weekday's
 * windows and a single date's are typed the same way and refused by the same
 * server check, so a pane with its own copy of this is the pane that drifts and
 * teaches the operator a second way to do what they already know.
 *
 * `label` names the day the windows belong to and every control repeats it. On
 * screen the row above already says which day; out of that context — a reader
 * listing this page's controls — five identical "Remove"s are a list nobody can
 * act on, and the week and the selected date are on one page together.
 *
 * What an empty set means is deliberately not decided here. To the week it is a
 * closed day, to a date it is a date offering nothing at all, and those are the
 * two panes' own sentences to write.
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
        // Keyed by position because a window has no identity of its own until it
        // is saved, and the ids the server gives them do not survive a set that
        // is replaced entire.
        <div className="row gap-2 wrap" key={position}>
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
              without halving the inputs. The week pane loses the danger
              colour and the label it had for that constraint too, even
              though its own column is fluid and had room to keep both —
              because this component is one shared act, not two, and a
              second markup for the same button here is exactly the drift
              its docstring exists to refuse. */}
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
        {/* Ghost and quiet rather than the bordered .btn it used to be —
            neither the selection nor the primary action, so the accent and
            full ink weight are not its to spend. The leading + is not
            decoration: WeekEditor passes its own ghost in beside this one
            (Use these hours on every open day), and two bare ghosts read as
            one piece of link text with nothing to tell them apart. */}
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
