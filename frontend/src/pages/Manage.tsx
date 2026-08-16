import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useLocation, useParams } from 'wouter'

import MonthGrid from '../components/MonthGrid'
import SlotList from '../components/SlotList'
import {
  asApiError,
  cancelBooking,
  DATABASE_BUSY,
  getBooking,
  getLink,
  getSlots,
  rescheduleBooking,
  type Booking,
  type Link,
} from '../lib/api'
import {
  endOf,
  longDate,
  monthCells,
  monthIndex,
  monthLabel,
  shiftMonth,
  shortDate,
  startOfMonth,
  todayIn,
  zonedDate,
  zonedTime,
} from '../lib/time'

const NOT_FOUND = 404
const PREVIOUS_MONTH = -1
const NEXT_MONTH = 1

/**
 * A write that waited out SQLite's busy timeout and gave up. Nothing happened,
 * which makes it the one failure here that is safe to repeat with the same
 * input — a 409 means "that instant is gone, choose another", this means "try
 * the same thing again shortly". The retry is left to the reader rather than
 * fired automatically: they are watching the button they just pressed, and a
 * silent retry would either look like nothing happened or fire a second write
 * they did not ask for.
 *
 * The wait comes off the server's own Retry-After, which `api.ts` has already
 * parsed. Inventing a number here would be a second opinion about a value the
 * server derives from the busy timeout it actually waited out.
 */
function busyMessage(seconds: number | null): string {
  const wait = seconds === null ? 'in a few seconds' : `in ${seconds} seconds`
  return `The server is busy and nothing was changed. Try again ${wait}.`
}

// Which whole-page answer the route is giving. `missing` is the one honest
// reply to any token that matches nothing — a real token for a deleted booking
// and a truncated one get the same page, because telling them apart tells a
// guesser which half of the token was right.
type Phase = 'loading' | 'ready' | 'missing' | 'broken'

// Which part of the card is open. Cancel is destructive and cannot be undone
// from this side, so it goes through `confirmCancel` first — a panel rather
// than a modal, because nothing here needs interrupting or protected focus.
type Panel = 'actions' | 'confirmCancel' | 'reschedule'

/**
 * `code` is null for a DataAnnotations 400, for a 400 the server sent with no
 * body at all, and for a request that never got an answer. All three are the
 * same thing to a reader: the move did not happen, and the recovery is to pick
 * again.
 */
function rescheduleMessage(code: string | null): string {
  switch (code) {
    case 'slot_taken':
      return 'Someone else took that time while you were choosing. Pick another.'
    case 'slot_unavailable':
      return 'That time is not on offer any more. Pick another.'
    default:
      return 'That time could not be booked. Pick another, or reload the page if it keeps happening.'
  }
}

function Page({ children, wide = false }: { children: ReactNode; wide?: boolean }) {
  return (
    <div className="page">
      {/* The calendar needs the card's full width; everything else reads better
          at measure. */}
      <main className={`card card-lg card-pad mt-6${wide ? '' : ' card-narrow'}`}>{children}</main>
      <footer className="fine page-foot">Scheduled with Kairos</footer>
    </div>
  )
}

function AlertIcon() {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" aria-hidden="true">
      <circle cx="12" cy="12" r="9" />
      <path d="M12 7.5v5M12 16h.01" />
    </svg>
  )
}

function Chevron({ back }: { back: boolean }) {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d={back ? 'M14.5 5.5L8 12l6.5 6.5' : 'M9.5 5.5L16 12l-6.5 6.5'} />
    </svg>
  )
}

/** The page someone opens from the link in their own booking. */
export default function Manage() {
  const { token } = useParams()
  const [, navigate] = useLocation()
  const [phase, setPhase] = useState<Phase>('loading')
  const [panel, setPanel] = useState<Panel>('actions')
  const [booking, setBooking] = useState<Booking | null>(null)
  const [link, setLink] = useState<Link | null>(null)
  const [month, setMonth] = useState('')
  const [day, setDay] = useState<string | null>(null)
  const [slots, setSlots] = useState<string[] | null>(null)
  const [start, setStart] = useState<string | null>(null)
  const [animate, setAnimate] = useState(false)
  const [message, setMessage] = useState('')
  const [busy, setBusy] = useState(false)
  const listRef = useRef<HTMLElement>(null)

  useEffect(() => {
    if (!token) {
      setPhase('missing')
      return
    }
    const controller = new AbortController()

    getBooking(token, controller.signal)
      .then(async (loaded) => {
        // The link carries two things the booking does not: what the meeting is
        // called, and the ORGANIZER's zone, which is the calendar the month grid
        // and the slots range are both keyed on. A link that has since been
        // deactivated costs the title and the ability to move the meeting; it
        // does not cost the page.
        const carrier = await getLink(loaded.slug, controller.signal).catch(() => null)
        if (controller.signal.aborted) return
        setBooking(loaded)
        setLink(carrier)
        setPhase('ready')
      })
      .catch((error: unknown) => {
        // Only a 404 is "this link leads nowhere". A 500 or a dropped
        // connection rendered as one would tell an invitee their meeting is
        // gone while it sits there intact.
        if (!controller.signal.aborted) {
          setPhase(asApiError(error).status === NOT_FOUND ? 'missing' : 'broken')
        }
      })

    return () => controller.abort()
  }, [token])

  useEffect(() => {
    if (panel !== 'reschedule' || booking === null || link === null || month === '') return

    // A month is at most 31 days, which is exactly the widest range the slots
    // endpoint answers for. One request per month, never one per day. The
    // duration is the booking's own and never changes: a different length would
    // be a different meeting, and holding it fixed is what lets the identifier
    // — and Part B's calendar UID — survive the move.
    const days = monthCells(month).filter((cell): cell is string => cell !== null)
    const controller = new AbortController()
    setSlots(null)

    getSlots(
      booking.slug,
      { from: days[0], to: days[days.length - 1], duration: booking.durationMinutes },
      controller.signal,
    )
      .then(setSlots)
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        const { status, code } = asApiError(error)
        // Corrupt stored availability is not a marker on one day. Nothing this
        // page could draw would be trustworthy, so the whole page says so.
        if (code === 'pattern_invalid') {
          setPhase('broken')
          return
        }
        setSlots([])
        setMessage(
          status === NOT_FOUND
            ? 'This link has stopped taking bookings, so the meeting cannot be moved. Ask the organizer for a new time.'
            : 'Times could not be loaded. Reload the page to try again.',
        )
      })

    return () => controller.abort()
  }, [panel, booking, link, month])

  // Keyed on the ORGANIZER's calendar, because that is what `from` and `to`
  // meant to the request that produced these instants, and therefore what a
  // square in the grid can be said to contain. The times inside a day render in
  // the invitee's zone and may legitimately fall on the day before or after.
  const grouped = useMemo(() => {
    const byDay = new Map<string, string[]>()
    const counts = new Map<string, number>()
    if (link === null || slots === null) return { byDay, counts }

    for (const instant of slots) {
      const key = zonedDate(instant, link.timeZone)
      const bucket = byDay.get(key)
      if (bucket === undefined) byDay.set(key, [instant])
      else bucket.push(instant)
    }
    for (const [key, list] of byDay) counts.set(key, list.length)

    return { byDay, counts }
  }, [link, slots])

  const runCancel = useCallback(async () => {
    if (!token) return
    setBusy(true)
    setMessage('')
    try {
      // Idempotent on the server, so a double tap or a second open of the same
      // link answers with the same row rather than a 404 for a booking the
      // reader can plainly see.
      setBooking(await cancelBooking(token))
      setPanel('actions')
    } catch (error) {
      const { status, code, retryAfter } = asApiError(error)
      // Whichever branch this takes, the panel stays open and the button stays
      // armed: cancel has no second option to offer, so a failure that closed
      // the panel would be a dead end, and one that showed the cancelled state
      // would be a lie about a booking that is still confirmed.
      if (status === NOT_FOUND) setPhase('missing')
      else if (code === DATABASE_BUSY) setMessage(busyMessage(retryAfter))
      else setMessage('The meeting could not be cancelled. Reload the page and try again.')
    } finally {
      setBusy(false)
    }
  }, [token])

  const runReschedule = useCallback(async () => {
    if (!token || start === null) return
    setBusy(true)
    setMessage('')
    try {
      // The chosen string goes back exactly as it arrived. Rebuilt as an offset
      // form it is refused as instant_invalid.
      setBooking(await rescheduleBooking(token, start))
      setPanel('actions')
      setStart(null)
      setSlots(null)
    } catch (error) {
      const { status, code, retryAfter } = asApiError(error)
      if (status === NOT_FOUND) {
        setPhase('missing')
      } else if (code === 'booking_cancelled') {
        // Cancelled somewhere else — another tab, or the organizer. The server
        // is right and this page is stale, so it stops offering the move.
        setBooking((current) => (current ? { ...current, status: 'cancelled' } : current))
        setPanel('actions')
      } else if (code === DATABASE_BUSY) {
        // The one failure that leaves the choice standing: the time was never
        // taken, so clearing it would send the reader hunting for a slot that
        // is still theirs.
        setMessage(busyMessage(retryAfter))
      } else {
        setMessage(rescheduleMessage(code))
        // Here the time really is gone, so it leaves the list rather than
        // sitting there to be picked a second time.
        setSlots((current) => current?.filter((instant) => instant !== start) ?? null)
        setStart(null)
      }
    } finally {
      setBusy(false)
    }
  }, [token, start])

  if (phase === 'loading') {
    return (
      <Page>
        <p className="prose">Loading your meeting…</p>
      </Page>
    )
  }

  if (phase === 'broken') {
    return (
      <Page>
        <h1 className="title-page">This page cannot be shown right now</h1>
        <p className="prose mt-3">
          The server is not answering as it should. Reload in a minute; if it keeps happening, the organizer is the one
          who can fix it.
        </p>
      </Page>
    )
  }

  if (phase === 'missing' || booking === null) {
    return (
      <Page>
        <h1 className="title-page">This link does not lead anywhere</h1>
        <p className="prose mt-3">
          The meeting may already have been removed, or the link may have been cut short when it was copied. Ask
          whoever sent it for a fresh one.
        </p>
      </Page>
    )
  }

  const zone = booking.timeZone
  const cancelled = booking.status === 'cancelled'
  const when = `${longDate(zonedDate(booking.start, zone))}, ${zonedTime(booking.start, zone)}–${zonedTime(booking.end, zone)}`
  const meta = `${booking.durationMinutes} minutes · Times in ${zone}`

  function openReschedule() {
    if (link === null) return
    setPanel('reschedule')
    setMonth(startOfMonth(todayIn(link.timeZone)))
    setDay(null)
    setStart(null)
    setMessage('')
    setAnimate(false)
  }

  function chooseDay(next: string) {
    setDay(next)
    setStart(null)
    setMessage('')
    setAnimate(true)
    // The SPA's debt: a server-rendered page would have moved focus by
    // navigating, and here nothing does unless it is written.
    listRef.current?.focus()
  }

  function changeMonth(delta: number) {
    setMonth((current) => shiftMonth(current, delta))
    setDay(null)
    setStart(null)
    setMessage('')
    setAnimate(true)
  }

  if (panel === 'reschedule' && link !== null) {
    // Derived rather than stored, so a day emptied by a refused move cannot
    // leave a stale selection behind.
    const activeDay = day !== null && grouped.byDay.has(day) ? day : (grouped.byDay.keys().next().value ?? null)
    const daySlots = activeDay === null ? [] : (grouped.byDay.get(activeDay) ?? [])
    const activeStart = start !== null && daySlots.includes(start) ? start : null
    const chosen =
      activeStart === null
        ? null
        : {
            long: `${longDate(zonedDate(activeStart, zone))}, ${zonedTime(activeStart, zone)}–${zonedTime(endOf(activeStart, booking.durationMinutes), zone)}`,
            short: `${shortDate(zonedDate(activeStart, zone))}, ${zonedTime(activeStart, zone)}`,
          }

    return (
      <>
        <Page wide>
          <button className="btn btn-ghost btn-inline mb-5" type="button" onClick={() => setPanel('actions')}>
            <Chevron back />
            Keep the time it has
          </button>

          <h1 className="title-page mb-3">Move this meeting</h1>
          <p className="prose mb-6 numeric">
            It stays {booking.durationMinutes} minutes long and keeps the same invitation, so your calendar updates the
            event you already have rather than adding a second one. Times in {zone}.
          </p>

          <div className="month-head">
            <button
              className="icon-btn"
              type="button"
              aria-label="Previous month"
              disabled={monthIndex(month) <= monthIndex(todayIn(link.timeZone))}
              onClick={() => changeMonth(PREVIOUS_MONTH)}
            >
              <Chevron back />
            </button>
            <span className="month-label numeric">{monthLabel(month)}</span>
            <button className="icon-btn" type="button" aria-label="Next month" onClick={() => changeMonth(NEXT_MONTH)}>
              <Chevron back={false} />
            </button>
          </div>

          {/* Two columns above 861px, one below, and the month capped at 300px
              rather than taking a free fraction — all of it in kairos.css. No
              resize handler measures anything here. */}
          <div className="calendar-cols">
            <MonthGrid month={month} counts={grouped.counts} selected={activeDay} onSelect={chooseDay} />

            <div>
              {slots === null ? (
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

              {/* Beside the times that failed, at every width. A banner at the
                  top of the page would be nowhere near them. */}
              {message !== '' && (
                <p className="field-error mt-4" role="alert">
                  <AlertIcon />
                  {message}
                </p>
              )}

              {chosen !== null && (
                <div className="desktop-cta">
                  <button className="btn btn-primary" type="button" data-loading={busy || undefined} onClick={runReschedule}>
                    Move to {chosen.long}
                  </button>
                </div>
              )}
            </div>
          </div>
        </Page>

        {/* Phone only, and decided by media query: this bar is display:none
            above 860px and the action above is display:none below it, so
            exactly one is ever visible and neither is chosen in JavaScript. It
            stays mounted while hidden so the page keeps reserving its height
            instead of jolting the list the moment a time is picked. */}
        <div className="actionbar" hidden={chosen === null}>
          <div className="grow">
            <p className="actionbar-when numeric">{chosen?.short}</p>
            <p className="actionbar-meta numeric">{meta}</p>
          </div>
          <button className="btn btn-primary" type="button" data-loading={busy || undefined} onClick={runReschedule}>
            Move
          </button>
        </div>
      </>
    )
  }

  return (
    <Page>
      <h1 className="title-event mb-4">{link?.name ?? booking.slug}</h1>

      {/* A dot as well as a word, filled against hollow: the state has to read
          without colour. */}
      <p className="status mb-5" data-state={cancelled ? 'paused' : 'live'}>
        {cancelled ? 'Cancelled' : 'Confirmed'}
      </p>

      {/* The accent wash says "this is your live commitment", so a cancelled
          meeting does not get it. The facts stay either way — an empty page
          would be the dishonest answer. */}
      <div className={cancelled ? 'group' : 'summary'}>
        <p className="summary-when numeric">{when}</p>
        <p className="summary-meta numeric">{meta}</p>
      </div>

      {/* One span, not three loose text nodes: `.fact` is a flex row with a
          12px gap, and left bare the words get that gap between them. */}
      <p className="fact mt-5">
        <span>
          Booked by <strong>{booking.name}</strong> · {booking.email}
        </span>
      </p>

      {cancelled ? (
        <>
          <p className="prose mt-5">
            This meeting was cancelled and the time is back on the organizer&apos;s calendar. Nothing further will
            arrive for it.
          </p>
          {/* A button rather than an anchor carrying `.btn`: the stylesheet
              defines `.btn` for controls and an underline for anchors, and
              combining them is a treatment it does not have. This way the one
              action on the page is a full 44px target and looks like every
              other control in the product. */}
          <button className="btn mt-5" type="button" onClick={() => navigate(`/book/${booking.slug}`)}>
            Book a new time
          </button>
        </>
      ) : panel === 'confirmCancel' ? (
        <div className="sep-top mt-6 group">
          <p className="prose">
            Cancel this meeting? The time goes back on the organizer&apos;s calendar straight away, and you cannot undo
            it from here.
          </p>
          <div className="row gap-2 wrap">
            <button className="btn btn-ghost btn-danger" type="button" data-loading={busy || undefined} onClick={runCancel}>
              Yes, cancel it
            </button>
            <button className="btn" type="button" onClick={() => setPanel('actions')}>
              Keep the meeting
            </button>
          </div>
          {message !== '' && (
            <p className="field-error" role="alert">
              <AlertIcon />
              {message}
            </p>
          )}
        </div>
      ) : (
        <>
          <div className="row gap-2 wrap mt-6">
            {link !== null && (
              <button className="btn" type="button" onClick={openReschedule}>
                Reschedule
              </button>
            )}
            <button className="btn btn-ghost btn-danger" type="button" onClick={() => setPanel('confirmCancel')}>
              Cancel meeting
            </button>
          </div>
          {/* Named rather than silently absent: an action that is simply not
              there reads as a bug. */}
          {link === null && (
            <p className="fine mt-3">
              This link has stopped taking bookings, so the meeting cannot be moved — only cancelled.
            </p>
          )}
        </>
      )}
    </Page>
  )
}
