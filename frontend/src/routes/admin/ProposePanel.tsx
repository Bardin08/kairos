import { useEffect, useMemo, useRef, useState } from 'react'

import MonthGrid from '../../components/MonthGrid'
import SlotList from '../../components/SlotList'
import { getSlots } from '../../lib/api'
import {
  monthCells,
  monthIndex,
  monthLabel,
  shiftMonth,
  shortDate,
  startOfMonth,
  todayIn,
  zonedDate,
  zonedTime,
} from '../../lib/time'
import { proposeMeeting, type Meeting } from './adminMeetings'
import Failure from './Failure'

const PREVIOUS_MONTH = -1
const NEXT_MONTH = 1

function Chevron({ back }: { back: boolean }) {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.8"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d={back ? 'M14.5 5.5L8 12l6.5 6.5' : 'M9.5 5.5L16 12l-6.5 6.5'} />
    </svg>
  )
}

/**
 * Choosing the time to offer, over the whole page rather than inside the row.
 *
 * It is its own screen for a measured reason. The first version of this put the
 * month grid and the slot column in the Actions cell of `RecordTable`, and a
 * browser measurement said what no unit test could: that cell is 215px wide at
 * 1280 and 178px at 899, `.calendar-cols` resolves its month track to 55px and
 * then 18px, and seven day columns inside that render the weekday headings on
 * top of each other at single-digit widths — 8px at 1280 and under 3px at 899.
 * Illegible, and beneath DESIGN.md's 44px tap floor by a factor of five.
 * Reproduced independently by review at the same two widths, which is why the
 * numbers are quoted rather than the impression. Nothing in kairos.css
 * expresses a 300px calendar inside a 200px cell, and rule 17 is not the reason
 * it was not written: no width would have fixed a control that does not fit the
 * container it was put in.
 *
 * So it swaps in over the list, which is the shape `Links.tsx` already uses for
 * `LinkEditor` and `Manage.tsx` for its own reschedule. Same components, same
 * classes, no new CSS, and the calendar gets the width it was drawn for.
 */
export default function ProposePanel({
  meeting,
  zone,
  onProposed,
  onCancel,
}: {
  meeting: Meeting
  /** The ORGANIZER's zone. Every time this panel draws or asks for is in it. */
  zone: string
  onProposed: (meeting: Meeting) => void
  onCancel: () => void
}) {
  const [month, setMonth] = useState(() => startOfMonth(todayIn(zone)))
  const [day, setDay] = useState<string | null>(null)
  const [slots, setSlots] = useState<string[] | null>(null)
  const [start, setStart] = useState<string | null>(null)
  const [animate, setAnimate] = useState(false)
  const [busy, setBusy] = useState(false)
  // Two failures, and they are not one state with two causes. A READ that never
  // arrived leaves nothing on screen to act on, so it replaces the times; a
  // refused SUGGESTION leaves every time still there and still pickable, so it
  // sits under them. Collapsing the two put an error where the calendar should
  // be and drew "nothing here has a gap this meeting fits into" over a dropped
  // connection — a claim about the operator's calendar made when nothing at all
  // was known about it.
  //
  // The error itself and not a sentence made from it either way: `Failure`
  // reads the status and the code off it, and a deactivated link, a slot taken
  // while the operator was choosing and a deployment with no mail relay are
  // three different things to do next.
  const [readFailure, setReadFailure] = useState<unknown>(null)
  const [failure, setFailure] = useState<unknown>(null)
  const listRef = useRef<HTMLElement>(null)

  useEffect(() => {
    // A month is at most 31 days, which is exactly the widest range the slots
    // endpoint answers for. One request per month, never one per day. The
    // duration is the meeting's own: the hold the server mints takes its length
    // from the booking whatever this asks for, so asking for another length
    // would only offer times the proposal could not keep.
    const days = monthCells(month).filter((cell): cell is string => cell !== null)
    const controller = new AbortController()
    setSlots(null)
    setReadFailure(null)

    getSlots(
      meeting.slug,
      { from: days[0], to: days[days.length - 1], duration: meeting.durationMinutes },
      controller.signal,
    )
      .then(setSlots)
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        // NOT setSlots([]). An empty month and a month nobody could read are
        // different answers, and the empty state's sentence — "nothing here has
        // a gap this meeting fits into" — is a claim about the calendar. Said
        // over a dropped connection it tells the operator this link is full
        // when nothing is known about it at all. Book.tsx draws its failure
        // INSTEAD of its empty state for the same reason.
        setSlots(null)
        setReadFailure(error)
      })

    // `zone` decides which day each instant is grouped under and which month
    // the picker opened on, so a zone that changed under this effect has to
    // re-ask rather than regroup a month that was never requested.
    return () => controller.abort()
  }, [month, zone, meeting.slug, meeting.durationMinutes])

  // Keyed on the ORGANIZER's calendar, because that is what `from` and `to`
  // meant to the request that produced these instants, and therefore what a
  // square in the grid can be said to contain. Here the times inside a day are
  // the organizer's too — this whole surface is theirs.
  const grouped = useMemo(() => {
    const byDay = new Map<string, string[]>()
    const counts = new Map<string, number>()
    if (slots === null) return { byDay, counts }

    for (const instant of slots) {
      const key = zonedDate(instant, zone)
      const bucket = byDay.get(key)
      if (bucket === undefined) byDay.set(key, [instant])
      else bucket.push(instant)
    }
    for (const [key, list] of byDay) counts.set(key, list.length)

    return { byDay, counts }
  }, [slots, zone])

  async function suggest(instant: string) {
    if (busy) return
    setBusy(true)
    setFailure(null)
    try {
      // The chosen string goes back exactly as it arrived. Rebuilt as an offset
      // form it is refused as instant_invalid.
      onProposed(await proposeMeeting(meeting.id, instant))
    } catch (error: unknown) {
      // The panel and the choice both stay. Every failure here leaves nothing
      // held — an unconfigured relay, a slot taken while the operator was
      // choosing, a hold somebody else opened — so sending them back to pick the
      // same time again would be busywork on top of a refusal.
      setFailure(error)
    } finally {
      setBusy(false)
    }
  }

  function changeMonth(delta: number) {
    setMonth((current) => shiftMonth(current, delta))
    setDay(null)
    setStart(null)
    setFailure(null)
    setAnimate(true)
  }

  function chooseDay(next: string) {
    setDay(next)
    setStart(null)
    setAnimate(true)
    // The SPA's debt: a server-rendered page would have moved focus by
    // navigating, and here nothing does unless it is written.
    listRef.current?.focus()
  }

  // Derived rather than stored, so a day emptied by a refused suggestion cannot
  // leave a stale selection behind.
  const activeDay =
    day !== null && grouped.byDay.has(day) ? day : (grouped.byDay.keys().next().value ?? null)
  const daySlots = activeDay === null ? [] : (grouped.byDay.get(activeDay) ?? [])
  const activeStart = start !== null && daySlots.includes(start) ? start : null

  return (
    <>
      <div className="page-header">
        <h1 className="title-page">Suggest a new time</h1>
        <button className="btn btn-ghost" type="button" onClick={onCancel}>
          Back
        </button>
      </div>

      <p className="fine mb-5">
        {meeting.participantName} is booked for {shortDate(zonedDate(meeting.start, zone))},{' '}
        {zonedTime(meeting.start, zone)}. Nothing moves until they accept — the time you pick is
        held for them in the meantime, and the meeting stays where it is if they say no. Times are
        the organizer&apos;s, in {zone}.
      </p>

      <div className="card card-pad">
        <div className="month-head">
          <button
            className="icon-btn"
            type="button"
            aria-label="Previous month"
            disabled={monthIndex(month) <= monthIndex(todayIn(zone))}
            onClick={() => changeMonth(PREVIOUS_MONTH)}
          >
            <Chevron back />
          </button>
          <span className="month-label numeric">{monthLabel(month)}</span>
          <button
            className="icon-btn"
            type="button"
            aria-label="Next month"
            onClick={() => changeMonth(NEXT_MONTH)}
          >
            <Chevron back={false} />
          </button>
        </div>

        {/* Two columns above 861px and one below, all of it in kairos.css. No
            resize handler measures anything here. */}
        <div className="calendar-cols">
          <MonthGrid
            month={month}
            counts={grouped.counts}
            selected={activeDay}
            onSelect={chooseDay}
          />

          <div>
            {/* The failed read comes first, and it is the whole point of the
                order: below it `slots === null` also means "still loading", and
                either of the two branches after it would be a sentence about a
                calendar this panel could not read. */}
            {readFailure !== null ? (
              <Failure error={readFailure} />
            ) : slots === null ? (
              <p className="prose t-sm">Finding open times…</p>
            ) : activeDay === null ? (
              <div className="empty">
                <p>Nothing in {monthLabel(month)} has a gap this meeting fits into.</p>
                <button className="btn" type="button" onClick={() => changeMonth(NEXT_MONTH)}>
                  Try {monthLabel(shiftMonth(month, NEXT_MONTH))}
                </button>
              </div>
            ) : (
              <SlotList
                ref={listRef}
                day={activeDay}
                slots={daySlots}
                selected={activeStart}
                zone={zone}
                animate={animate}
                resetKey={activeDay}
                onSelect={setStart}
              />
            )}

            {/* A refusal of the SUGGESTION, under the times it was made against
                and never instead of them. Suppressed while a read has failed, so
                one broken month cannot stack two errors on one column. */}
            {readFailure === null && <Failure error={failure} className="mt-4" />}

            {/* Absent until a time is picked rather than present and disabled: a
                button that cannot be pressed still reads as an action, and there
                is no action here yet. */}
            {activeStart !== null && (
              // A plain row rather than Manage.tsx's `.desktop-cta`, and the
              // difference is measured: kairos.css hides that class below 861px
              // because the manage page pairs it with a fixed `.actionbar` on a
              // phone. The admin shell has a tab bar in that space and reserves
              // its own room for it, so borrowing the desktop half alone would
              // leave the operator on a phone able to pick a time and with no
              // button to send it.
              <div className="row gap-2 wrap mt-5">
                <button
                  className="btn btn-primary"
                  type="button"
                  data-loading={busy || undefined}
                  onClick={() => suggest(activeStart)}
                  // The label is a verb, so the time it acts on has to reach a
                  // screen reader some other way. Described by, not labelled by:
                  // the button is still named "Suggest this time".
                  aria-describedby="propose-when"
                >
                  Suggest this time
                </button>
                <span className="muted t-sm numeric" id="propose-when">
                  {shortDate(zonedDate(activeStart, zone))}, {zonedTime(activeStart, zone)}
                </span>
              </div>
            )}
          </div>
        </div>
      </div>
    </>
  )
}
