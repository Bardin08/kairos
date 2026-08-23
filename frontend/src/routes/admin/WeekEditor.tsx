import { useState } from 'react'

import { BLANK, clock, windowsOf, type Draft } from './adminTime'
import Failure from './Failure'
import { replacePatternRules, type Pattern, type PatternRule } from './patterns'
import WindowRows from './WindowRows'

/** Monday first, indexed by ISO day − 1. */
const WEEKDAYS = [
  'Monday',
  'Tuesday',
  'Wednesday',
  'Thursday',
  'Friday',
  'Saturday',
  'Sunday',
]

/** ISO-8601: 1 is Monday, 7 is Sunday. Not `Date.getDay()`, whose Sunday is 0. */
const MONDAY = 1

/** The seven days, Monday first, each holding its windows in the order shown. */
type Week = Draft[][]

/** One window as the rules endpoint takes it. No id: the set is replaced entire. */
type Hours = { isoDayOfWeek: number; startMinute: number; endMinute: number }

/** The pattern's rules as seven days of windows, earliest first within each day. */
function weekOf(rules: readonly PatternRule[]): Week {
  const week: Week = WEEKDAYS.map(() => [])

  for (const rule of [...rules].sort((left, right) => left.startMinute - right.startMinute)) {
    // A miss is a rule dropped, not an editor that will not render.
    // `isoDayOfWeek` has no CHECK constraint behind it, so a row outside 1-7 is
    // storable — and `AvailabilityQuery.cs:108` makes the same decision on the
    // server for the same reason.
    const day = week[rule.isoDayOfWeek - MONDAY]
    if (day === undefined) continue

    day.push({ start: clock(rule.startMinute), end: clock(rule.endMinute) })
  }

  return week
}

/**
 * The exact set a save would send, or null while any window is unfinished.
 *
 * Days in week order and windows in the order the pane shows them, so the
 * request reads the way the screen does and a refusal naming a day can be found
 * on it. A closed day contributes nothing, which is the whole of how closure is
 * expressed: there is no flag to send, only an absence.
 */
function rulesOf(week: Week): Hours[] | null {
  const rules: Hours[] = []

  for (const [index, day] of week.entries()) {
    const windows = windowsOf(day)
    if (windows === null) return null

    rules.push(...windows.map((window) => ({ isoDayOfWeek: index + MONDAY, ...window })))
  }

  return rules
}

/**
 * A pattern's week: seven rows, Monday to Sunday, always all seven.
 *
 * A day with no windows is a closed day. There is no closed flag in the data and
 * no close button on the screen, because removing a day's last window and
 * closing the day are one act — a row that has run out of windows draws itself
 * as closed and offers to be opened again.
 *
 * Edits are made against a draft and sent in one PUT when the operator asks. A
 * pattern in use is live, so a keystroke in a time field that widened a window
 * would be an hour on offer the moment it was typed, and a half-typed one would
 * be refused as a window ending before it starts. The endpoint has the same
 * shape — it stores the whole set or refuses the whole set.
 *
 * Whether two windows overlap is the server's answer and not this pane's. The
 * same bound guards the booking engine, and a second copy here is the copy that
 * goes stale; what this owns is putting the server's sentence where the operator
 * pressed.
 */
export default function WeekEditor({
  pattern,
  onChange,
}: {
  pattern: Pattern
  /** Handed the whole pattern the write answered with, never a patch of it. */
  onChange: (updated: Pattern) => void
}) {
  const [source, setSource] = useState(pattern.rules)
  const [week, setWeek] = useState(() => weekOf(pattern.rules))
  const [unsaved, setUnsaved] = useState(false)
  const [saving, setSaving] = useState(false)
  // The error itself, not a sentence made from it: `Failure` reads the status
  // off it, and a string has already thrown that away.
  const [failure, setFailure] = useState<unknown>(null)

  // A save and a reload both arrive as a new rules array, and both are the
  // week — so the draft is replaced by what came back rather than kept over it.
  if (source !== pattern.rules) {
    setSource(pattern.rules)
    setWeek(weekOf(pattern.rules))
    setUnsaved(false)
  }

  const rules = rulesOf(week)

  function edit(next: Week) {
    setWeek(next)
    setUnsaved(true)
    // A refusal is about a set nobody is proposing any more.
    setFailure(null)
  }

  /** The one way a day changes. Handed no windows, the day is closed. */
  function setDay(index: number, windows: Draft[]) {
    edit(week.map((day, candidate) => (candidate === index ? windows : day)))
  }

  /**
   * That day's windows onto every day that is already open, closed days left
   * closed — this sets hours and never decides which days a pattern works. The
   * windows are copied rather than shared, so editing one day afterwards cannot
   * move the rest of the week with it.
   */
  function applyToEveryOpenDay(index: number) {
    edit(week.map((day) => (day.length === 0 ? day : week[index].map((draft) => ({ ...draft })))))
  }

  function save() {
    if (rules === null || !unsaved || saving) return

    setSaving(true)
    setFailure(null)

    replacePatternRules(pattern.id, rules)
      .then((updated) => {
        setSaving(false)
        setUnsaved(false)
        onChange(updated)
      })
      .catch((error: unknown) => {
        setSaving(false)
        setFailure(error)
      })
  }

  return (
    <section className="card card-pad">
      <h2 className="title-section mb-4">Weekly hours</h2>

      {/* Seven rows of one-word buttons, so every control carries its own day in
          its name — the rule WindowRows follows for the same reason, and the
          reason a reader listing this page's controls can act on the list. */}
      <ul className="group">
        {WEEKDAYS.map((name, index) => {
          const windows = week[index]

          return (
            <li className="week-day" key={name}>
              <span className="week-name">{name}</span>

              {windows.length === 0 ? (
                <div className="row gap-2">
                  {/* The word, not only the dimming: a row that is quieter than
                      the ones above it says nothing to a reader who cannot see
                      the difference. */}
                  <span className="grow muted t-sm">Closed</span>
                  <button
                    className="btn"
                    type="button"
                    aria-label={`Open ${name}`}
                    onClick={() => setDay(index, [BLANK])}
                  >
                    Open
                  </button>
                </div>
              ) : (
                <WindowRows
                  label={name}
                  windows={windows}
                  onChange={(next) => setDay(index, next)}
                >
                  <button
                    className="btn btn-ghost"
                    type="button"
                    aria-label={`${name}: Use these hours on every open day`}
                    onClick={() => applyToEveryOpenDay(index)}
                  >
                    Use these hours on every open day
                  </button>
                </WindowRows>
              )}
            </li>
          )
        })}
      </ul>

      <div className="row gap-2 wrap sep-top">
        <button
          className="btn btn-primary"
          type="button"
          aria-disabled={unsaved && rules !== null ? undefined : true}
          data-loading={saving || undefined}
          onClick={save}
        >
          Save weekly hours
        </button>
        {/* Words rather than a colour on the button, and the reason the button
            is refusing rather than only the fact of it. */}
        {rules === null ? (
          <span className="fine">Every window needs an opening and a closing time</span>
        ) : (
          unsaved && <span className="fine">Not saved yet</span>
        )}
      </div>

      <Failure error={failure} className="mt-3" />
    </section>
  )
}
