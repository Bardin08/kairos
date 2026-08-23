import { useEffect, useState, type FormEvent } from 'react'
import { Link } from 'wouter'

import { getSettings } from './adminSettings'
import { clock } from './adminTime'
import Failure from './Failure'
import {
  createPattern,
  deletePattern,
  listPatterns,
  type Pattern,
  type PatternRule,
} from './patterns'

/**
 * Monday first, indexed by ISO day − 1. Short in the chip and long in the
 * sentence beside it: the chips are a picture of the week, and the sentence is
 * what a screen reader gets.
 */
const WEEKDAYS = [
  { short: 'Mo', long: 'Mon' },
  { short: 'Tu', long: 'Tue' },
  { short: 'We', long: 'Wed' },
  { short: 'Th', long: 'Thu' },
  { short: 'Fr', long: 'Fri' },
  { short: 'Sa', long: 'Sat' },
  { short: 'Su', long: 'Sun' },
]

const MONDAY = 1

/**
 * `Mon, Tue, Wed · 09:00–17:00` — the card's one line of hours. Distinct windows
 * rather than one per rule: a Monday-to-Friday nine-to-five is five rules and
 * one sentence, and a split day is two windows however many days carry it.
 */
function summarize(rules: PatternRule[]): string {
  const days = WEEKDAYS.filter((_, index) =>
    rules.some((rule) => rule.isoDayOfWeek === index + MONDAY),
  ).map((weekday) => weekday.long)

  const windows = rules
    .slice()
    .sort((left, right) => left.startMinute - right.startMinute)
    .map((rule) => `${clock(rule.startMinute)}–${clock(rule.endMinute)}`)

  return `${days.join(', ')} · ${[...new Set(windows)].join(', ')}`
}

/** `Mentorship, Consulting and Quick sync`. */
function listNames(names: string[]): string {
  if (names.length < 2) return names.join('')
  return `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`
}

/**
 * The operator's patterns, as a wall of read-only cards.
 *
 * Everything that changes one pattern lives at `/admin/availability/{id}`; this
 * page holds only what is true of the set — what exists, what each one is for,
 * and which links would break if one went away. No booking and no link editing
 * either: both have been proposed for this tab, and either one turns a reusable
 * shape into a diary of exactly one.
 */
export default function Availability() {
  const [patterns, setPatterns] = useState<Pattern[]>([])
  const [loading, setLoading] = useState(true)
  const [naming, setNaming] = useState(false)
  const [name, setName] = useState('')
  const [creating, setCreating] = useState(false)
  // The error itself, not a sentence made from it: `Failure` branches on the
  // status, and a string has already thrown that away.
  const [failure, setFailure] = useState<unknown>(null)
  const [deleting, setDeleting] = useState<string | null>(null)
  // Kept per pattern: the refusal names that pattern's links, and means nothing
  // over the top of a grid of cards.
  const [refused, setRefused] = useState<{ patternId: string; error: unknown } | null>(null)
  // Empty until it arrives, so the hours line reads as hours alone rather than
  // as hours followed by a gap where a zone should be.
  const [zone, setZone] = useState('')

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
        setFailure(error)
      })

    // Its own request and its own silence: a zone that does not arrive costs
    // this page a suffix, and one that turned the page red would be a failure
    // report about a label on a screen whose subject is elsewhere.
    getSettings(controller.signal)
      .then((settings) => setZone(settings.timeZone))
      .catch(() => undefined)

    return () => controller.abort()
  }, [])

  function create(event: FormEvent) {
    event.preventDefault()
    if (name.trim() === '') return

    setCreating(true)
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
        setNaming(false)
        setCreating(false)
      })
      .catch((error: unknown) => {
        setCreating(false)
        setFailure(error)
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
    setDeleting(patternId)
    setRefused(null)

    deletePattern(patternId)
      .then(() => {
        setPatterns((list) => list.filter((candidate) => candidate.id !== patternId))
        setDeleting(null)
      })
      .catch((error: unknown) => {
        setDeleting(null)
        setRefused({ patternId, error })
      })
  }

  return (
    <>
      <div className="page-header">
        <h1 className="title-page">Availability</h1>
        <button
          className="btn btn-primary"
          type="button"
          aria-expanded={naming}
          onClick={() => setNaming(!naming)}
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

      {/* A name is all a pattern needs to exist; its hours are set in the editor
          the new card links to. In a card rather than loose in the header,
          because the header is a heading and one control here as everywhere. */}
      {naming && (
        <form className="card card-pad stack mb-5" onSubmit={create}>
          <div className="field">
            <label htmlFor="pattern-name">Name</label>
            <input
              id="pattern-name"
              className="input"
              placeholder="Weekdays 09–17"
              value={name}
              autoFocus
              onChange={(event) => setName(event.target.value)}
            />
            <p className="fine">What you will pick this shape by when you point a link at it.</p>
          </div>
          <div className="row gap-2 wrap">
            <button
              className="btn btn-primary"
              type="submit"
              aria-disabled={name.trim() === '' ? true : undefined}
              data-loading={creating || undefined}
            >
              Create pattern
            </button>
            <button className="btn btn-ghost" type="button" onClick={() => setNaming(false)}>
              Cancel
            </button>
          </div>
        </form>
      )}

      <Failure error={failure} className="mb-5" />

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

              {/* A picture of the week, and only a picture: the line under it
                  names the same days in words, so nothing is carried by fill. */}
              <div className="pattern-days mb-3" aria-hidden="true">
                {WEEKDAYS.map((weekday, index) => (
                  <span
                    className="pattern-day"
                    data-on={
                      pattern.rules.some((rule) => rule.isoDayOfWeek === index + MONDAY)
                        ? 'true'
                        : undefined
                    }
                    key={weekday.short}
                  >
                    {weekday.short}
                  </span>
                ))}
              </div>

              {pattern.rules.length === 0 ? (
                <p className="muted t-sm">No hours yet — nothing bookable.</p>
              ) : (
                // The zone on every card rather than once at the top: a card is
                // the unit somebody screenshots, and 09:00–17:00 with no zone
                // on it is a time that means four different things.
                <p className="muted numeric t-sm">
                  {summarize(pattern.rules)}
                  {zone === '' ? '' : ` · ${zone}`}
                </p>
              )}

              <p className="fine sep-top">
                {pattern.usedBy.length === 0
                  ? 'Not used by any link yet'
                  : `Used by ${listNames(pattern.usedBy)}`}
              </p>

              <div className="row gap-2 mt-4">
                <Link className="btn grow" href={`/availability/${pattern.id}`}>
                  Edit
                </Link>
                <button
                  className="btn btn-ghost btn-danger"
                  type="button"
                  data-loading={deleting === pattern.id || undefined}
                  onClick={() => remove(pattern.id)}
                >
                  Delete
                </button>
              </div>

              <Failure
                error={refused?.patternId === pattern.id ? refused.error : null}
                className="mt-3"
              />
            </article>
          ))}
        </div>
      )}
    </>
  )
}
