import { useEffect, useId, useMemo, useRef, useState } from 'react'

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
import {
  cancelMeeting,
  proposeMeeting,
  withdrawProposal,
  type Meeting,
  type MeetingProposal,
} from './adminMeetings'
import Failure from './Failure'

// Which panel this row has open. Cancel is destructive and cannot be undone
// from here, so it goes through `confirm` first — a panel rather than a modal,
// because nothing here needs interrupting or protected focus. The same shape
// the invitee's manage page uses for the same decision, and `propose` is that
// page's reschedule with the commit turned into a question.
type Panel = 'none' | 'confirm' | 'propose'

// `CancelMeetingRequest.MaxReasonLength` on the server, which answers 400 above
// it. Enforced here as well so the bound is a limit the field stops at rather
// than a rejection after the fact.
const MAX_REASON = 2000

// The one proposal state that is still a question. Everything else has been
// answered, has lapsed or was taken back, and the row's answer to all of those
// is to offer a fresh suggestion rather than to withdraw a closed one. The
// server folds the clock into this word, so a hold whose expiry has passed
// arrives as `expired` with nothing having swept it.
const OPEN = 'open'

const DECLINED = 'declined'
const EXPIRED = 'expired'

const PREVIOUS_MONTH = -1
const NEXT_MONTH = 1

/**
 * What became of a closed suggestion, or null when there is nothing to report.
 *
 * A refusal and a silence are two different facts about the invitee and they
 * decide different things — whether the operator chases them — so one sentence
 * for both would be worse than saying nothing. `withdrawn` is silent on purpose:
 * the operator took it back themselves, and reporting somebody's own action
 * back to them is noise on a row that has other news to carry.
 */
function outcome(proposal: MeetingProposal | null): string | null {
  if (proposal === null) return null
  if (proposal.state === DECLINED) return 'They declined the suggested time.'
  if (proposal.state === EXPIRED) return 'A suggested time lapsed with no answer.'
  return null
}

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

/** What one meeting's row lets the operator do to it. */
export default function MeetingActions({
  meetingId,
  slug,
  durationMinutes,
  zone,
  proposal,
  onChanged,
}: {
  meetingId: string
  /** The link's slug. `GET /links/{slug}/slots` takes no link id, so the id will not do. */
  slug: string
  /** The meeting's own length, held fixed: a different length is a different meeting. */
  durationMinutes: number
  /** The ORGANIZER's zone. Every time this component draws or asks for is in it. */
  zone: string
  /** The newest hold on this meeting, or null if there has never been one. */
  proposal: MeetingProposal | null
  onChanged: (meeting: Meeting) => void
}) {
  const [panel, setPanel] = useState<Panel>('none')
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)
  // The error itself and not a sentence made from it: `Failure` reads the
  // status and the code off it, and a 400 from the length bound, a 503 that
  // changed nothing and a dead network are three different things to do next.
  // One generic line for all three tells the operator to retry the one thing
  // retrying cannot fix.
  const [failure, setFailure] = useState<unknown>(null)
  const [month, setMonth] = useState('')
  const [day, setDay] = useState<string | null>(null)
  const [slots, setSlots] = useState<string[] | null>(null)
  const [start, setStart] = useState<string | null>(null)
  const [animate, setAnimate] = useState(false)
  // One row's field among many on the page, so the id cannot be written down.
  const reasonId = useId()
  const listRef = useRef<HTMLElement>(null)

  useEffect(() => {
    if (panel !== 'propose' || month === '') return

    // A month is at most 31 days, which is exactly the widest range the slots
    // endpoint answers for. One request per month, never one per day. The
    // duration is the meeting's own: the hold the server mints takes its length
    // from the booking whatever this asks for, so asking for another length
    // would only offer times the proposal could not keep.
    const days = monthCells(month).filter((cell): cell is string => cell !== null)
    const controller = new AbortController()
    setSlots(null)

    getSlots(
      slug,
      { from: days[0], to: days[days.length - 1], duration: durationMinutes },
      controller.signal,
    )
      .then(setSlots)
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        // The server's own sentence rather than one made here — a deactivated
        // link, corrupt stored availability and a dead network are three
        // different things for the operator to do next, and `Failure` is where
        // that branch already lives.
        setSlots([])
        setFailure(error)
      })

    return () => controller.abort()
  }, [panel, month, slug, durationMinutes])

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

  function close() {
    // Cleared on the way out, not on the way in: a panel reopened later would
    // otherwise come back holding the words the operator abandoned and the
    // error from the attempt they gave up on.
    setPanel('none')
    setReason('')
    setFailure(null)
    setStart(null)
    setDay(null)
    setSlots(null)
  }

  async function confirm() {
    if (busy) return
    setBusy(true)
    setFailure(null)
    try {
      const cancelled = await cancelMeeting(meetingId, reason)
      close()
      onChanged(cancelled)
    } catch (error: unknown) {
      // The panel stays open and the button stays armed. Cancel has no second
      // option to offer, so a failure that closed the panel would be a dead end.
      setFailure(error)
    } finally {
      setBusy(false)
    }
  }

  async function suggest(instant: string) {
    if (busy) return
    setBusy(true)
    setFailure(null)
    try {
      // The chosen string goes back exactly as it arrived. Rebuilt as an offset
      // form it is refused as instant_invalid.
      const proposed = await proposeMeeting(meetingId, instant)
      close()
      onChanged(proposed)
    } catch (error: unknown) {
      // Panel and choice both stay: every failure here leaves nothing held —
      // an unconfigured relay, a slot taken while the operator was choosing, a
      // hold somebody else opened — so sending them back to pick the same time
      // again would be busywork on top of a refusal.
      setFailure(error)
    } finally {
      setBusy(false)
    }
  }

  async function withdraw() {
    if (busy || proposal === null) return
    setBusy(true)
    setFailure(null)
    try {
      onChanged(await withdrawProposal(meetingId, proposal.id))
    } catch (error: unknown) {
      setFailure(error)
    } finally {
      setBusy(false)
    }
  }

  function openPropose() {
    setPanel('propose')
    setMonth(startOfMonth(todayIn(zone)))
    setDay(null)
    setStart(null)
    setFailure(null)
    setAnimate(false)
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

  if (panel === 'propose') {
    // Derived rather than stored, so a day emptied by a refused suggestion
    // cannot leave a stale selection behind.
    const activeDay =
      day !== null && grouped.byDay.has(day)
        ? day
        : (grouped.byDay.keys().next().value ?? null)
    const daySlots = activeDay === null ? [] : (grouped.byDay.get(activeDay) ?? [])
    const activeStart = start !== null && daySlots.includes(start) ? start : null

    return (
      <div className="group">
        <p className="record-meta">
          Nothing moves until the invitee accepts. The time is held for them in the meantime.
        </p>

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

        {/* Two columns where there is room for them and one where there is not,
            all of it in kairos.css. No resize handler measures anything here. */}
        <div className="calendar-cols">
          <MonthGrid
            month={month}
            counts={grouped.counts}
            selected={activeDay}
            onSelect={chooseDay}
          />

          <div>
            {slots === null ? (
              <p className="prose t-sm">Finding open times…</p>
            ) : activeDay === null ? (
              <div className="empty">
                <p>Nothing in {monthLabel(month)} has a gap this meeting fits into.</p>
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
          </div>
        </div>

        <Failure error={failure} />

        <div className="row gap-2 wrap">
          {/* Absent until a time is picked rather than present and disabled: a
              button that cannot be pressed still reads as an action, and there
              is no action here yet. */}
          {activeStart !== null && (
            <button
              className="btn btn-primary"
              type="button"
              data-loading={busy || undefined}
              onClick={() => suggest(activeStart)}
            >
              Suggest this time
            </button>
          )}
          <button className="btn" type="button" onClick={close}>
            Leave it where it is
          </button>
        </div>
      </div>
    )
  }

  if (panel === 'confirm') {
    return (
      <div className="group">
        <div className="field">
          <label htmlFor={reasonId}>Why, in your words</label>
          <textarea
            className="textarea"
            id={reasonId}
            // The server's own bound, so 2100 characters is a key that does not
            // type rather than a 400 the operator is invited to retry.
            maxLength={MAX_REASON}
            value={reason}
            onChange={(event) => setReason(event.target.value)}
          />
          <span className="fine">The invitee reads this in the cancellation.</span>
        </div>
        <Failure error={failure} />
        <div className="row gap-2">
          <button
            className="btn btn-ghost btn-danger"
            type="button"
            onClick={confirm}
            data-loading={busy || undefined}
          >
            Call it off
          </button>
          <button className="btn" type="button" onClick={close}>
            Keep it
          </button>
        </div>
      </div>
    )
  }

  // A hold that is still a question. Only one may be open at a time — the
  // server answers proposal_open — so this row offers to take it back rather
  // than to make a second suggestion that would be refused.
  if (proposal !== null && proposal.state === OPEN) {
    return (
      <div className="group">
        <p className="record-meta numeric">
          Suggested {shortDate(zonedDate(proposal.start, zone))}, {zonedTime(proposal.start, zone)}
        </p>
        <p className="record-meta numeric">
          Lapses {shortDate(zonedDate(proposal.expiresAt, zone))}
        </p>
        <Failure error={failure} />
        <button
          className="btn"
          type="button"
          onClick={withdraw}
          data-loading={busy || undefined}
        >
          Withdraw
        </button>
      </div>
    )
  }

  const news = outcome(proposal)

  return (
    <div className="group">
      {/* What became of the last suggestion, next to the button that makes
          another one — which is the operator's usual next move after either.
          It is here rather than beside the meeting's own status because the
          meeting's status did not change: a refused or lapsed suggestion leaves
          a confirmed meeting exactly where it was. */}
      {news !== null && <p className="record-meta">{news}</p>}
      <Failure error={failure} />
      <div className="row gap-2 wrap">
        <button className="btn" type="button" onClick={openPropose}>
          Suggest a new time
        </button>
        <button
          className="btn btn-ghost btn-danger"
          type="button"
          onClick={() => setPanel('confirm')}
        >
          Cancel
        </button>
      </div>
    </div>
  )
}
