import { useState } from 'react'

import AlertIcon from './AlertIcon'

/**
 * The four lengths nearly every link uses.
 *
 * A custom value attaches to the link that asked for it and never joins this
 * menu: most links want one of these, and the one that needs 25 or 50 gets it
 * without every other link's picker growing a row it will never pick.
 */
const PRESETS = [15, 30, 45, 60]

const MIN_MINUTES = 1
const MAX_MINUTES = 1440

const CUSTOM = 'custom'
const PLACEHOLDER = ''

/**
 * The lengths a link is offered at, as chips plus a way to add one.
 *
 * Order is the operator's and is kept: it becomes `SortOrder` on the way to the
 * database and then the order of the booking page's tabs, which is not the same
 * list as ascending length.
 *
 * Every refusal is stated beside the control rather than in a banner, and the
 * one that matters most is the last chip: a link with no durations passes every
 * constraint SQLite can express — the rule spans two tables — and then kills the
 * public booking page on a message that names an index.
 */
export default function DurationEditor({
  durations,
  onChange,
}: {
  durations: number[]
  onChange: (durations: number[]) => void
}) {
  const [custom, setCustom] = useState('')
  const [asking, setAsking] = useState(false)
  const [problem, setProblem] = useState('')

  function add(minutes: number) {
    if (durations.includes(minutes)) {
      setProblem(`This link already offers ${minutes} minutes.`)
      return
    }

    setProblem('')
    onChange([...durations, minutes])
  }

  function addCustom() {
    const minutes = Number(custom)

    // `Number` is what rejects "25.5" and "" alike — the first is not an
    // integer, the second is 0 and falls under the floor. Both land on the same
    // sentence because the recovery is the same.
    if (!Number.isInteger(minutes) || minutes < MIN_MINUTES || minutes > MAX_MINUTES) {
      setProblem(
        `A duration is a whole number of minutes between ${MIN_MINUTES} and ${MAX_MINUTES}.`,
      )
      return
    }

    add(minutes)
    setCustom('')
    setAsking(false)
  }

  function remove(minutes: number) {
    if (durations.length === 1) {
      setProblem('A link must offer at least one duration.')
      return
    }

    setProblem('')
    onChange(durations.filter((offered) => offered !== minutes))
  }

  function pick(chosen: string) {
    if (chosen === PLACEHOLDER) return

    if (chosen === CUSTOM) {
      setProblem('')
      setAsking(true)
      return
    }

    setAsking(false)
    add(Number(chosen))
  }

  return (
    <div className="field">
      <label id="durations-label">Durations</label>

      <div
        className="row gap-2 wrap"
        id="durations"
        role="group"
        aria-labelledby="durations-label"
      >
        {durations.map((minutes) => (
          <span className="chip numeric" key={minutes}>
            {minutes} min
            <button
              className="chip-x"
              type="button"
              aria-label={`Remove ${minutes} minutes`}
              onClick={() => remove(minutes)}
            >
              ×
            </button>
          </span>
        ))}

        {/* Held at "" so the menu returns to its prompt after every pick — it
            is an action, not a selection, and a select that stayed on "30 min"
            would read as the link's state rather than as the last thing added.
            Dressed as a chip, at chip height: the old .select-inline was 36px
            beside 44px chips and the row never lined up. The placeholder now
            reads "+ Add" rather than a prompt, which looks like a button — but
            it is still `option value={PLACEHOLDER}`, so picking it is still
            the no-op `pick` guards on with its first line; do not "fix" that
            guard into a real option. */}
        <select
          className="chip-add"
          value={PLACEHOLDER}
          aria-label="Add a duration"
          onChange={(event) => pick(event.target.value)}
        >
          <option value={PLACEHOLDER}>+ Add</option>
          {PRESETS.map((minutes) => (
            <option key={minutes} value={minutes}>
              {minutes} min
            </option>
          ))}
          <option value={CUSTOM}>Add custom…</option>
        </select>

        {asking && (
          <>
            <span className="affix">
              <input
                className="input numeric ctl-num"
                type="number"
                min={MIN_MINUTES}
                max={MAX_MINUTES}
                step={1}
                value={custom}
                aria-label="Custom duration in minutes"
                onChange={(event) => setCustom(event.target.value)}
              />
              <span className="affix-unit" aria-hidden="true">
                min
              </span>
            </span>
            <button className="btn" type="button" onClick={addCustom}>
              Add
            </button>
          </>
        )}
      </div>

      {problem !== '' && (
        <p className="field-error" role="alert">
          <AlertIcon />
          {problem}
        </p>
      )}
    </div>
  )
}
