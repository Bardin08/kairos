import { useEffect, useId, useState } from 'react'

import AlertIcon from '../../components/AlertIcon'
import { asApiError } from '../../lib/api'
import {
  addPatternRule,
  createPattern,
  deletePattern,
  listPatterns,
  removePatternOverride,
  removePatternRule,
  renamePattern,
  savePatternOverride,
  type Pattern,
  type PatternOverride,
  type PatternRule,
} from './patterns'

/**
 * The seven chips on a pattern card, Monday first, indexed by ISO day − 1.
 * Short in the chip and long in the sentence beside it, because the chips are
 * a picture of the week and the sentence is what a screen reader gets.
 */
const WEEKDAYS = [
  { short: 'Mo', long: 'Mon', full: 'Monday' },
  { short: 'Tu', long: 'Tue', full: 'Tuesday' },
  { short: 'We', long: 'Wed', full: 'Wednesday' },
  { short: 'Th', long: 'Thu', full: 'Thursday' },
  { short: 'Fr', long: 'Fri', full: 'Friday' },
  { short: 'Sa', long: 'Sat', full: 'Saturday' },
  { short: 'Su', long: 'Sun', full: 'Sunday' },
]

const MINUTES_PER_HOUR = 60
const LAST_HOUR = 23
const MONDAY = 1

// The hours an operator most often means, so the add form opens on a working
// day rather than on 00:00–00:00, which the server would refuse.
const DEFAULT_START = '09:00'
const DEFAULT_END = '17:00'

/** Which action is in flight, and which one a failure belongs under. */
type Action = 'create' | 'name' | 'rule' | 'override' | 'delete'

/** `540` → `09:00`. Zero-padded, so a column of them keeps one width. */
function clock(minuteOfDay: number): string {
  const hour = Math.floor(minuteOfDay / MINUTES_PER_HOUR)
  const minute = minuteOfDay % MINUTES_PER_HOUR
  return `${String(hour).padStart(2, '0')}:${String(minute).padStart(2, '0')}`
}

/**
 * `09:00` → `540`, and null for anything else.
 *
 * `<input type="time">` answers with an empty string while it is empty and
 * with `HH:MM` once it is not, so null here means "not filled in yet" and is
 * what keeps the Add button from offering to send a half-typed window.
 */
function minuteOfDay(value: string): number | null {
  const match = /^(\d{2}):(\d{2})$/.exec(value)
  if (match === null) return null

  const hour = Number(match[1])
  const minute = Number(match[2])
  if (hour > LAST_HOUR || minute >= MINUTES_PER_HOUR) return null

  return hour * MINUTES_PER_HOUR + minute
}

/** `09:00–17:00`, an en dash, as every window in this product is written. */
function windowText(startMinute: number, endMinute: number): string {
  return `${clock(startMinute)}–${clock(endMinute)}`
}

/** `Mon, Tue, Wed` — the days a pattern is open, in week order. */
function openDays(rules: PatternRule[]): string[] {
  return WEEKDAYS
    .filter((_, index) => rules.some((rule) => rule.isoDayOfWeek === index + MONDAY))
    .map((weekday) => weekday.long)
}

/**
 * The card's one line of hours.
 *
 * Distinct windows rather than one per rule: a Monday-to-Friday nine-to-five is
 * five rules and one sentence, and a split day is two windows however many days
 * carry it. The chips above already say which days.
 */
function hours(rules: PatternRule[]): string {
  const windows = rules
    .slice()
    .sort((left, right) => left.startMinute - right.startMinute)
    .map((rule) => windowText(rule.startMinute, rule.endMinute))

  return [...new Set(windows)].join(', ')
}

/** A date override in one phrase. The blocked case reads as a closure, not as missing data. */
function describeOverride(replacement: PatternOverride): string {
  if (replacement.startMinute === null || replacement.endMinute === null) {
    return 'blocked — nothing bookable'
  }

  return windowText(replacement.startMinute, replacement.endMinute)
}

/** `Mentorship, Consulting and Quick sync`. */
function listNames(names: string[]): string {
  if (names.length < 2) return names.join('')
  return `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`
}

/**
 * The message under the control that produced it.
 *
 * DESIGN.md refuses a banner at the top of a form, and the refusals this page
 * gets are the reason why: "this pattern is the availability of Mentorship"
 * means something under the Delete button and nothing at the top of a card
 * that also has a name field and two add forms in it.
 */
function Failure({ message }: { message: string | null }) {
  if (message === null) return null

  return (
    <p className="field-error mt-3" role="alert">
      <AlertIcon />
      {message}
    </p>
  )
}

/**
 * One pattern's whole editable surface, opened in place inside its own card.
 *
 * Not a modal, on purpose: DESIGN.md refuses one for a task that needs neither
 * interruption nor protected focus, and this is a task an operator abandons
 * halfway more often than they finish it. Remounted per pattern — `editing` is
 * a single id — so the draft fields reset when a different card is opened
 * rather than carrying one pattern's half-typed window into another's form.
 */
function PatternEditor({
  pattern,
  pending,
  failure,
  apply,
  remove,
  close,
}: {
  pattern: Pattern
  pending: Action | null
  failure: { action: Action; message: string } | null
  apply: (action: Action, work: () => Promise<Pattern>) => void
  remove: () => void
  close: () => void
}) {
  const [name, setName] = useState(pattern.name)
  const [isoDayOfWeek, setIsoDayOfWeek] = useState(MONDAY)
  const [ruleStart, setRuleStart] = useState(DEFAULT_START)
  const [ruleEnd, setRuleEnd] = useState(DEFAULT_END)
  const [date, setDate] = useState('')
  // Blocked first, and true by default. A date override exists mostly because
  // somebody is taking a day off; the form that opens on "different hours"
  // makes the common case the one you have to go and find.
  const [blocked, setBlocked] = useState(true)
  const [overrideStart, setOverrideStart] = useState(DEFAULT_START)
  const [overrideEnd, setOverrideEnd] = useState(DEFAULT_END)

  const field = useId()

  const ruleFrom = minuteOfDay(ruleStart)
  const ruleTo = minuteOfDay(ruleEnd)
  const overrideFrom = minuteOfDay(overrideStart)
  const overrideTo = minuteOfDay(overrideEnd)

  const canAddRule = ruleFrom !== null && ruleTo !== null
  const canSaveOverride =
    date !== '' && (blocked || (overrideFrom !== null && overrideTo !== null))

  // Whether the window is the right way round is the server's answer, not this
  // form's. It holds the same bound the booking page reads availability
  // through, and a second copy here is the copy that goes stale.
  function addRule() {
    if (ruleFrom === null || ruleTo === null) return
    apply('rule', () =>
      addPatternRule(pattern.id, {
        isoDayOfWeek,
        startMinute: ruleFrom,
        endMinute: ruleTo,
      }),
    )
  }

  function saveOverride() {
    if (!canSaveOverride) return
    apply('override', () =>
      savePatternOverride(pattern.id, date, {
        // Both null is the blocked day. Sent as nulls rather than omitted,
        // because the endpoint refuses one time without the other and this is
        // the shape that says "neither" out loud.
        startMinute: blocked ? null : overrideFrom,
        endMinute: blocked ? null : overrideTo,
      }),
    )
  }

  function messageFor(action: Action): string | null {
    return failure?.action === action ? failure.message : null
  }

  return (
    <div className="stack mt-4 sep-top">
      <div className="field">
        <label htmlFor={`${field}-name`}>Name</label>
        <div className="row gap-2">
          <input
            className="input grow"
            id={`${field}-name`}
            value={name}
            onChange={(event) => setName(event.target.value)}
          />
          <button
            className="btn"
            type="button"
            data-loading={pending === 'name' || undefined}
            onClick={() => apply('name', () => renamePattern(pattern.id, name))}
          >
            Rename
          </button>
        </div>
        <Failure message={messageFor('name')} />
      </div>

      <div>
        <h3 className="title-section mb-3">Weekly hours</h3>

        {pattern.rules.length === 0 ? (
          <div className="empty mb-3">
            <p>No hours yet, so every link on this pattern offers nothing. Add a window below.</p>
          </div>
        ) : (
          <ul className="stack mb-3">
            {pattern.rules.map((rule) => (
              <li className="row gap-2" key={rule.id}>
                <span className="grow numeric t-sm">
                  {WEEKDAYS[rule.isoDayOfWeek - MONDAY].long}{' '}
                  {windowText(rule.startMinute, rule.endMinute)}
                </span>
                <button
                  className="btn btn-ghost btn-danger"
                  type="button"
                  onClick={() => apply('rule', () => removePatternRule(pattern.id, rule.id))}
                >
                  Remove
                </button>
              </li>
            ))}
          </ul>
        )}

        <div className="row gap-2 wrap">
          <select
            className="select select-inline"
            aria-label="Day of the week"
            value={isoDayOfWeek}
            onChange={(event) => setIsoDayOfWeek(Number(event.target.value))}
          >
            {WEEKDAYS.map((weekday, index) => (
              <option key={weekday.full} value={index + MONDAY}>
                {weekday.full}
              </option>
            ))}
          </select>
          <input
            className="input"
            type="time"
            aria-label="Opens at"
            value={ruleStart}
            onChange={(event) => setRuleStart(event.target.value)}
          />
          <input
            className="input"
            type="time"
            aria-label="Closes at"
            value={ruleEnd}
            onChange={(event) => setRuleEnd(event.target.value)}
          />
          <button
            className="btn"
            type="button"
            aria-disabled={canAddRule ? undefined : true}
            data-loading={pending === 'rule' || undefined}
            onClick={addRule}
          >
            Add window
          </button>
        </div>
        <Failure message={messageFor('rule')} />
      </div>

      <div>
        <h3 className="title-section mb-3">Single dates</h3>
        <p className="fine mb-3">
          A date here replaces the weekly hours rather than adding to them. Blocked means the day
          is closed entirely — that is how a holiday is taken.
        </p>

        {pattern.overrides.length > 0 && (
          <ul className="stack mb-3">
            {pattern.overrides.map((replacement) => (
              <li className="row gap-2" key={replacement.date}>
                <span className="grow numeric t-sm">
                  {replacement.date} · {describeOverride(replacement)}
                </span>
                <button
                  className="btn btn-ghost btn-danger"
                  type="button"
                  onClick={() =>
                    apply('override', () =>
                      removePatternOverride(pattern.id, replacement.date),
                    )
                  }
                >
                  Remove
                </button>
              </li>
            ))}
          </ul>
        )}

        <div className="row gap-2 wrap">
          <input
            className="input"
            type="date"
            aria-label="Date"
            value={date}
            onChange={(event) => setDate(event.target.value)}
          />
          {/* Two named choices rather than a pair of time fields somebody has
              to guess they may leave empty. A form that only offered "change
              the hours" could not say "closed", which is the case this whole
              section exists for. */}
          <div className="segmented" role="group" aria-label="What this date does">
            <button type="button" aria-pressed={blocked} onClick={() => setBlocked(true)}>
              Blocked
            </button>
            <button type="button" aria-pressed={!blocked} onClick={() => setBlocked(false)}>
              Different hours
            </button>
          </div>
          {!blocked && (
            <>
              <input
                className="input"
                type="time"
                aria-label="Opens at on this date"
                value={overrideStart}
                onChange={(event) => setOverrideStart(event.target.value)}
              />
              <input
                className="input"
                type="time"
                aria-label="Closes at on this date"
                value={overrideEnd}
                onChange={(event) => setOverrideEnd(event.target.value)}
              />
            </>
          )}
          <button
            className="btn"
            type="button"
            aria-disabled={canSaveOverride ? undefined : true}
            data-loading={pending === 'override' || undefined}
            onClick={saveOverride}
          >
            Set date
          </button>
        </div>
        <Failure message={messageFor('override')} />
      </div>

      <div className="row gap-2 wrap sep-top">
        <button className="btn grow" type="button" onClick={close}>
          Done
        </button>
        <button
          className="btn btn-ghost btn-danger"
          type="button"
          data-loading={pending === 'delete' || undefined}
          onClick={remove}
        >
          Delete pattern
        </button>
      </div>
      <Failure message={messageFor('delete')} />
    </div>
  )
}

/**
 * The operator's availability: patterns, the weekly windows inside them, and
 * the single dates that deviate.
 *
 * A pattern is a reusable shape, not a schedule of things that will happen.
 * There is deliberately no booking on this screen and no way to edit a link
 * from it — which pattern a link stands on is the link's own field, and a
 * booking is something that already happened to a shape. Both have been
 * proposed for this tab, and either one turns a reusable object into a diary of
 * exactly one.
 */
export default function Availability() {
  const [patterns, setPatterns] = useState<Pattern[]>([])
  const [loading, setLoading] = useState(true)
  const [editing, setEditing] = useState<string | null>(null)
  const [name, setName] = useState('')
  const [pending, setPending] = useState<Action | null>(null)
  const [failure, setFailure] = useState<{ action: Action; message: string } | null>(null)

  useEffect(() => {
    const controller = new AbortController()

    listPatterns(controller.signal)
      .then((list) => {
        setPatterns(list)
        setLoading(false)
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        setLoading(false)
        setFailure({ action: 'create', message: asApiError(error).message })
      })

    return () => controller.abort()
  }, [])

  /**
   * A write, and the one copy of what happens around one.
   *
   * Every write answers with the whole pattern, so the card is replaced rather
   * than patched — which is what keeps `usedBy` and the rule list from drifting
   * out of step with the row a mutation actually changed.
   */
  function apply(action: Action, work: () => Promise<Pattern>) {
    setPending(action)
    setFailure(null)

    work()
      .then((updated) => {
        setPatterns((list) =>
          list.map((candidate) => (candidate.id === updated.id ? updated : candidate)),
        )
        setPending(null)
      })
      .catch((error: unknown) => {
        setPending(null)
        setFailure({ action, message: asApiError(error).message })
      })
  }

  function create() {
    if (name.trim() === '') return
    setPending('create')
    setFailure(null)

    createPattern(name)
      .then((created) => {
        // Inserted in name order, which is the order the list came back in. A
        // new card appearing at the bottom and jumping on the next load reads
        // as the page having lost it.
        setPatterns((list) =>
          [...list, created].sort((left, right) => left.name.localeCompare(right.name)),
        )
        setName('')
        setPending(null)
        setEditing(created.id)
      })
      .catch((error: unknown) => {
        setPending(null)
        setFailure({ action: 'create', message: asApiError(error).message })
      })
  }

  /**
   * Refused while any link stands on the pattern, and the refusal names them.
   * Never softened into a confirmation: a cascade would leave those links
   * active, resolvable and offering nothing, which reads as an outage rather
   * than as a deletion somebody chose. The server's own sentence is shown
   * verbatim because it is the one that has the link names in it.
   */
  function remove(patternId: string) {
    setPending('delete')
    setFailure(null)

    deletePattern(patternId)
      .then(() => {
        setPatterns((list) => list.filter((candidate) => candidate.id !== patternId))
        setPending(null)
        setEditing(null)
      })
      .catch((error: unknown) => {
        setPending(null)
        setFailure({ action: 'delete', message: asApiError(error).message })
      })
  }

  return (
    <>
      <div className="page-header">
        <h1 className="title-page">Availability</h1>
        <div className="row gap-2">
          <input
            className="input"
            aria-label="Name of the new pattern"
            placeholder="Weekdays 09–17"
            value={name}
            onChange={(event) => setName(event.target.value)}
          />
          <button
            className="btn btn-primary"
            type="button"
            aria-disabled={name.trim() === '' ? true : undefined}
            data-loading={pending === 'create' || undefined}
            onClick={create}
          >
            <svg
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              strokeLinecap="round"
              aria-hidden="true"
            >
              <path d="M12 5v14M5 12h14" />
            </svg>
            New pattern
          </button>
        </div>
      </div>

      {/* Said once, at the top: the times below are wall-clock in the
          organizer's own zone. Which zone that is lives in Settings and is not
          repeated here, because a second place to read it is a second place for
          it to be wrong. */}
      <p className="fine mb-5">
        Every time on this page is your own wall clock. The zone it is read in is in Settings.
      </p>

      {failure?.action === 'create' && (
        <p className="field-error mb-5" role="alert">
          <AlertIcon />
          {failure.message}
        </p>
      )}

      {loading ? (
        <p className="prose t-sm">Reading your patterns…</p>
      ) : patterns.length === 0 ? (
        <div className="empty">
          <p>
            No patterns yet. A pattern is the weekly shape a link books against — name one above,
            then give it its hours.
          </p>
        </div>
      ) : (
        <div className="grid-cards">
          {patterns.map((pattern) => (
            <article className="card card-pad-sm" key={pattern.id}>
              <h2 className="title-section mb-3">{pattern.name}</h2>

              {/* A picture of the week, and only a picture: the sentence under
                  it names the same days in words, so nothing here is carried by
                  the chip's fill alone. */}
              <div className="pattern-days mb-3" aria-hidden="true">
                {WEEKDAYS.map((weekday, index) => (
                  <span
                    className="pattern-day"
                    data-on={
                      pattern.rules.some((rule) => rule.isoDayOfWeek === index + MONDAY)
                        ? 'true'
                        : undefined
                    }
                    key={weekday.full}
                  >
                    {weekday.short}
                  </span>
                ))}
              </div>

              {pattern.rules.length === 0 ? (
                <p className="muted t-sm">No hours yet — nothing bookable.</p>
              ) : (
                <p className="muted numeric t-sm">
                  {openDays(pattern.rules).join(', ')} · {hours(pattern.rules)}
                </p>
              )}

              {pattern.overrides.length > 0 && (
                <p className="fine mt-3">
                  {pattern.overrides.length === 1
                    ? '1 single date set'
                    : `${pattern.overrides.length} single dates set`}
                </p>
              )}

              <p className="fine sep-top">
                {pattern.usedBy.length === 0
                  ? 'Not used by any link yet'
                  : `Used by ${listNames(pattern.usedBy)}`}
              </p>

              {editing === pattern.id ? (
                <PatternEditor
                  pattern={pattern}
                  pending={pending}
                  failure={failure}
                  apply={apply}
                  remove={() => remove(pattern.id)}
                  close={() => setEditing(null)}
                />
              ) : (
                <div className="row gap-2 mt-4">
                  <button className="btn" type="button" onClick={() => setEditing(pattern.id)}>
                    Edit
                  </button>
                </div>
              )}
            </article>
          ))}
        </div>
      )}
    </>
  )
}
