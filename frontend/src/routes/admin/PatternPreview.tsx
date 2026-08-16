import { useEffect, useState } from 'react'

import MonthGrid from '../../components/MonthGrid'
import { getSlots } from '../../lib/api'
import {
  localZone,
  longDate,
  monthLabel,
  shiftMonth,
  shortDate,
  startOfMonth,
  todayIn,
  zonedDate,
} from '../../lib/time'
import { listLinks, type AdminLink } from './adminLinks'
import { getSettings } from './adminSettings'
import { clock, minuteOfDay, shiftDays } from './adminTime'
import Failure from './Failure'
import { patternDays } from './patternDays'
import {
  removePatternOverride,
  savePatternOverride,
  type Pattern,
  type PatternOverride,
} from './patterns'

const PREVIOUS_MONTH = -1
const NEXT_MONTH = 1
const DAY_BEFORE = -1

/** Blocking a date is an override with no hours in it at all. */
const BLOCKED = { startMinute: null, endMinute: null }

/** Two empty time fields: nothing typed yet, which is not the same as 00:00. */
const NO_HOURS = { start: '', end: '' }

/** The picker's own value. Not a slug, so no link can ever collide with it. */
const DAYS_ONLY = ''

/** Slots per date key on the ORGANIZER's calendar, which is what a square is. */
function countByDay(slots: readonly string[], zone: string): Map<string, number> {
  const counts = new Map<string, number>()

  for (const slot of slots) {
    const day = zonedDate(slot, zone)
    counts.set(day, (counts.get(day) ?? 0) + 1)
  }

  return counts
}

/** What one date does instead of the week. */
function describe(override: PatternOverride | undefined): string {
  if (override === undefined) return 'Follows the weekly hours'
  // Both minutes null is the date blocked entirely. A pair of them is the day
  // replaced, which opens it however the week reads.
  if (override.startMinute === null || override.endMinute === null) return 'Blocked'
  return `${clock(override.startMinute)}–${clock(override.endMinute)}`
}

/**
 * What this pattern actually offers, as a month.
 *
 * Two readings of the same pattern, and the split is the whole design. By
 * default the month shows DAYS — open, closed, blocked — and no times at all,
 * which is the only picture that is true of every link standing on the pattern
 * and is still true of one no link points at yet. Times are on demand: pick a
 * link and the squares fill with that link's real bookable starts, fetched from
 * the same public endpoint an invitee's browser calls.
 *
 * The times are fetched rather than computed here, and that is not laziness
 * about a day of work. Margin, minimum notice, daily cap and start increment
 * differ per link while a pattern is shared, so a single "what an invitee sees"
 * would be a lie about every link but one; and a second implementation of them
 * in the browser is the one that drifts from the engine that takes the booking.
 * What is computed locally is the part that cannot drift — see patternDays.ts.
 */
export default function PatternPreview({ pattern }: { pattern: Pattern }) {
  /*
   * The overrides this pane writes are kept here rather than handed back to the
   * page, and that is a decision about the pane beside it rather than about
   * this one. An override write answers with the whole pattern, a fresh `rules`
   * array included; WeekEditor reads a new `rules` array as a reload and
   * replaces its draft with it. Handing the answer up would therefore make
   * blocking a holiday throw away a week somebody was halfway through typing.
   * Nothing outside this pane reads `overrides`, so the copy that diverges is
   * the copy nobody asks — and the moment the week IS saved, the server's
   * answer arrives here as a new `pattern.overrides` and takes over again.
   */
  const [source, setSource] = useState(pattern.overrides)
  const [overrides, setOverrides] = useState(pattern.overrides)

  // Which month opens is not the same question as which date a square holds:
  // this is the operator's own calendar, and at a month boundary the worst it
  // can do is open the neighbouring month of a grid with arrows on it.
  const [month, setMonth] = useState(() => startOfMonth(todayIn(localZone())))
  const [selected, setSelected] = useState<string | null>(null)
  const [draft, setDraft] = useState(NO_HOURS)
  const [saving, setSaving] = useState(false)
  // The error itself, not a sentence made from it: `Failure` reads the status
  // off it, and a string has already thrown that away.
  const [failure, setFailure] = useState<unknown>(null)

  // The links standing on this pattern and the zone their times are counted in.
  // One state, because they arrive together and neither is usable alone.
  const [catalog, setCatalog] = useState<{ links: AdminLink[]; zone: string } | null>(null)
  const [catalogFailure, setCatalogFailure] = useState<unknown>(null)
  const [slug, setSlug] = useState(DAYS_ONLY)
  // Null is "not read yet", never "no times": the difference is what keeps an
  // unanswered request from drawing a month that says nothing is bookable.
  const [counts, setCounts] = useState<Map<string, number> | null>(null)
  const [slotsFailure, setSlotsFailure] = useState<unknown>(null)
  const [attempt, setAttempt] = useState(0)

  if (source !== pattern.overrides) {
    setSource(pattern.overrides)
    setOverrides(pattern.overrides)
  }

  const links = catalog?.links ?? []
  const chosen = links.find((link) => link.slug === slug)
  const zone = catalog?.zone ?? ''

  const usedByCount = pattern.usedBy.length

  useEffect(() => {
    // A pattern no link points at has nothing to offer times for, and the two
    // requests would be two refusable calls made to fill a picker with nothing.
    if (usedByCount === 0) return

    const controller = new AbortController()

    Promise.all([listLinks(controller.signal), getSettings(controller.signal)])
      .then(([page, settings]) => {
        setCatalog({
          // By pattern id rather than by matching `usedBy` names, which is the
          // same set arrived at without depending on two links never sharing a
          // name. Archived links are left out: their public slots route 404s,
          // so offering one would be offering a preview that cannot load.
          links: page.links.filter((link) => link.patternId === pattern.id && link.active),
          zone: settings.timeZone,
        })
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        setCatalogFailure(error)
      })

    return () => controller.abort()
  }, [pattern.id, usedByCount])

  useEffect(() => {
    setCounts(null)
    setSlotsFailure(null)
    if (chosen === undefined) return

    const controller = new AbortController()
    const first = startOfMonth(month)
    // Day zero of the next month. The grid's own span, so the request is never
    // wider than the 31 days the endpoint accepts.
    const last = shiftDays(shiftMonth(month, NEXT_MONTH), DAY_BEFORE)

    getSlots(
      chosen.slug,
      // The first duration, which is the one the booking page opens on. A link
      // offering several is previewed on that one rather than on all of them,
      // because the endpoint answers for one at a time and a union of them is
      // not a day anybody can book.
      { from: first, to: last, duration: chosen.durations[0] },
      controller.signal,
    )
      .then((slots) => setCounts(countByDay(slots, zone)))
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        setSlotsFailure(error)
      })

    return () => controller.abort()
  }, [chosen, month, zone, attempt])

  const states = patternDays({ month, rules: pattern.rules, overrides })

  function overrideOn(day: string): PatternOverride | undefined {
    return overrides.find((override) => override.date === day)
  }

  // Takes the request unsent so that the one refusal to make a second one while
  // the first is in flight can live here, rather than at each of the three
  // controls that write — and the one that forgot it would be the one that
  // removed an override twice.
  function write(request: () => Promise<Pattern>) {
    if (saving) return

    setSaving(true)
    setFailure(null)

    request()
      .then((updated) => {
        setOverrides(updated.overrides)
        setSaving(false)
        // That day is a different day now, so whatever times were counted for
        // it belong to a pattern that no longer exists.
        setAttempt((current) => current + 1)
      })
      .catch((error: unknown) => {
        setSaving(false)
        setFailure(error)
      })
  }

  /**
   * One click, one exception. A date the pattern has said nothing about becomes
   * blocked; a date it has said anything about goes back to the weekly hours.
   *
   * The write is immediate and there is no confirmation, because the inverse is
   * the same gesture on the same square — and a pattern in use is live, so a
   * dialog between the click and the block would be a holiday still on offer
   * while somebody reads it.
   */
  function toggle(day: string) {
    setSelected(day)
    setDraft(NO_HOURS)
    write(() =>
      overrideOn(day) === undefined
        ? savePatternOverride(pattern.id, day, BLOCKED)
        : removePatternOverride(pattern.id, day),
    )
  }

  /** Opens a date without changing it — how an exception is read rather than set. */
  function choose(day: string) {
    const override = overrideOn(day)

    setMonth(startOfMonth(day))
    setSelected(day)
    setFailure(null)
    setDraft(
      override === undefined || override.startMinute === null || override.endMinute === null
        ? NO_HOURS
        : { start: clock(override.startMinute), end: clock(override.endMinute) },
    )
  }

  function applyHours() {
    const startMinute = minuteOfDay(draft.start)
    const endMinute = minuteOfDay(draft.end)
    if (selected === null || startMinute === null || endMinute === null) return

    write(() => savePatternOverride(pattern.id, selected, { startMinute, endMinute }))
  }

  const hours = minuteOfDay(draft.start) !== null && minuteOfDay(draft.end) !== null

  return (
    <section className="card card-pad">
      <div className="month-head">
        <h2 className="title-section">Preview</h2>
        <div className="row gap-2">
          <button
            className="icon-btn"
            type="button"
            aria-label="Previous month"
            onClick={() => setMonth(shiftMonth(month, PREVIOUS_MONTH))}
          >
            <svg
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="1.8"
              strokeLinecap="round"
              strokeLinejoin="round"
              aria-hidden="true"
            >
              <path d="M14.5 5.5L8 12l6.5 6.5" />
            </svg>
          </button>
          <span className="month-label numeric">{monthLabel(month)}</span>
          <button
            className="icon-btn"
            type="button"
            aria-label="Next month"
            onClick={() => setMonth(shiftMonth(month, NEXT_MONTH))}
          >
            <svg
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="1.8"
              strokeLinecap="round"
              strokeLinejoin="round"
              aria-hidden="true"
            >
              <path d="M9.5 5.5L16 12l-6.5 6.5" />
            </svg>
          </button>
        </div>
      </div>

      {/* Only where there is something to pick. A pattern with no links has one
          honest reading and no choice to make about it. */}
      {links.length > 0 && (
        <div className="field mb-4">
          <label htmlFor="preview-as">Show</label>
          <select
            id="preview-as"
            className="select"
            value={slug}
            onChange={(event) => setSlug(event.target.value)}
          >
            <option value={DAYS_ONLY}>Days only</option>
            {links.map((link) => (
              <option key={link.id} value={link.slug}>
                {link.name}
              </option>
            ))}
          </select>
          <p className="fine">
            Days are true of every link on this pattern. A link&apos;s times are its own: they
            carry its margin, its minimum notice and its daily cap.
          </p>
        </div>
      )}

      <Failure error={catalogFailure} className="mb-4" />

      {slotsFailure !== null ? (
        // Never a month with nothing in it. An empty grid and a link that
        // offers nothing are the same picture, and one of them is a lie.
        <div>
          <Failure error={slotsFailure} />
          <button
            className="btn mt-3"
            type="button"
            onClick={() => setAttempt((current) => current + 1)}
          >
            Try again
          </button>
        </div>
      ) : chosen !== undefined && counts === null ? (
        <p className="prose t-sm">Reading the times for {chosen.name}…</p>
      ) : (
        <MonthGrid
          month={month}
          counts={counts ?? undefined}
          states={states}
          selected={selected}
          onSelect={toggle}
        />
      )}

      <p className="fine mt-3">
        Click a date to block it. Click a blocked date to put it back on the weekly hours.
      </p>

      {selected !== null && (
        <div className="sep-top">
          <div className="row row-between gap-2 wrap">
            <strong className="t-sm numeric">{longDate(selected)}</strong>
            <span className="muted t-sm numeric">
              {saving ? 'Saving…' : describe(overrideOn(selected))}
            </span>
          </div>

          {/* Different hours on one date, reached from the date itself. Each
              field names the date it belongs to: on screen the line above says
              which, but a reader listing this page's controls gets two fields
              called "opens at" and no way to tell them from the week's. */}
          <div className="row gap-2 wrap mt-3">
            <input
              className="input numeric week-time"
              type="time"
              aria-label={`${longDate(selected)}, opens at`}
              value={draft.start}
              onChange={(event) => setDraft({ ...draft, start: event.target.value })}
            />
            <span aria-hidden="true">–</span>
            <input
              className="input numeric week-time"
              type="time"
              aria-label={`${longDate(selected)}, closes at`}
              value={draft.end}
              onChange={(event) => setDraft({ ...draft, end: event.target.value })}
            />
            <button
              className="btn"
              type="button"
              aria-disabled={hours ? undefined : true}
              data-loading={saving || undefined}
              onClick={applyHours}
            >
              Use these hours on this date
            </button>
          </div>

          {overrideOn(selected) !== undefined && (
            <button
              className="btn btn-ghost mt-3"
              type="button"
              data-loading={saving || undefined}
              onClick={() => write(() => removePatternOverride(pattern.id, selected))}
            >
              Back to the weekly hours
            </button>
          )}

          <Failure error={failure} className="mt-3" />
        </div>
      )}

      {/* The third state, which the grid cannot draw: a date with its own hours
          is an open day and looks like one. Listed rather than marked, because
          the list is also the only way to reach an exception in a month nobody
          is looking at — and reading one here does not change it, which a click
          on the square would. */}
      {overrides.length > 0 && (
        <div className="sep-top">
          <h3 className="title-section mb-3">Exceptions</h3>
          <ul className="group">
            {[...overrides]
              .sort((left, right) => left.date.localeCompare(right.date))
              .map((override) => (
                <li className="row gap-2 wrap" key={override.date}>
                  {/* Named for what pressing it does, and named in full: the
                      square for the same date is a few lines up the page with
                      an abbreviation of the same words on it, and a list of
                      controls that has both in it has to say which is which. */}
                  <button
                    className="btn btn-ghost btn-inline numeric"
                    type="button"
                    aria-label={`Show ${longDate(override.date)}`}
                    aria-pressed={override.date === selected}
                    onClick={() => choose(override.date)}
                  >
                    {shortDate(override.date)}
                  </button>
                  <span className="muted numeric t-sm">{describe(override)}</span>
                </li>
              ))}
          </ul>
        </div>
      )}
    </section>
  )
}
