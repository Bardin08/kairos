import { useEffect, useMemo, useRef, useState } from 'react'
import { useParams } from 'wouter'

import BookingForm from '../components/BookingForm'
import DurationPicker from '../components/DurationPicker'
import MonthGrid from '../components/MonthGrid'
import SlotList from '../components/SlotList'
import {
  asApiError,
  getLink,
  getSlots,
  type ApiError,
  type Booking,
  type Link,
} from '../lib/api'
import {
  endOf,
  localZone,
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

// One corrupt stored availability rule kills every range containing that
// weekday, so there is no day to mark and no month to page to. It is the whole
// page's problem, not the slot column's.
const CORRUPT_AVAILABILITY = 'pattern_invalid'

// Both link routes answer this for a slug that resolves to nothing — including
// a slug no link could ever have. Keyed on the code and never on the title:
// the two routes word that sentence differently.
const NO_SUCH_LINK = 'not_found'

const NEXT_MONTH = 1
const PREVIOUS_MONTH = -1
const NOT_FOUND = 404

type Step = 'choose' | 'details' | 'done'

function AlertIcon() {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.8"
      strokeLinecap="round"
      aria-hidden="true"
    >
      <circle cx="12" cy="12" r="9" />
      <path d="M12 7.5v5M12 16h.01" />
    </svg>
  )
}

function notice(title: string, body: string) {
  return (
    <div className="card card-lg card-pad card-narrow">
      <h1 className="title-page">{title}</h1>
      <p className="prose mt-3">{body}</p>
    </div>
  )
}

/**
 * The page a stranger opens from a pasted link.
 *
 * Two zones are in play throughout and mixing them is the bug worth naming
 * once: the month grid is keyed on the ORGANIZER's calendar, because that is
 * what `from` and `to` mean to the slots endpoint, while every time on screen
 * is rendered in the INVITEE's. The instant itself is never re-derived — the
 * string the server sent is the string that comes back to it.
 */
export default function Book() {
  const { slug = '' } = useParams()

  // Read once, at mount. A zone that changed mid-booking would silently move
  // every time on screen out from under a selection already made.
  const [zone] = useState(localZone)

  const [link, setLink] = useState<Link | null>(null)
  const [linkError, setLinkError] = useState<ApiError | null>(null)

  const [month, setMonth] = useState(() => startOfMonth(todayIn(zone)))
  const [duration, setDuration] = useState<number | null>(null)
  const [attempt, setAttempt] = useState(0)

  const [slots, setSlots] = useState<string[] | null>(null)
  const [slotsError, setSlotsError] = useState<ApiError | null>(null)

  const [day, setDay] = useState<string | null>(null)
  const [start, setStart] = useState<string | null>(null)
  const [animate, setAnimate] = useState(false)
  const [lost, setLost] = useState<string | null>(null)

  const [step, setStep] = useState<Step>('choose')
  const [booked, setBooked] = useState<Booking | null>(null)

  const listRef = useRef<HTMLElement>(null)

  useEffect(() => {
    const controller = new AbortController()
    setLink(null)
    setLinkError(null)

    getLink(slug, controller.signal)
      .then((loaded) => {
        // An aborted fetch rejects, so this is belt and braces — but the catch
        // below guards the same way, and a success path that does not is the
        // asymmetry someone later reads as "the abort is decorative".
        if (controller.signal.aborted) return
        setLink(loaded)
        // The link's own order, so the first entry is the one whoever
        // configured it put first — not the shortest.
        setDuration(loaded.durations[0] ?? null)
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setLinkError(asApiError(error))
      })

    return () => controller.abort()
  }, [slug])

  useEffect(() => {
    if (link === null || duration === null) return

    // A month is at most 31 days, which is exactly the widest range the slots
    // endpoint answers for. One request per month, never one per day.
    const days = monthCells(month).filter((cell): cell is string => cell !== null)
    const controller = new AbortController()
    setSlots(null)
    setSlotsError(null)

    getSlots(
      link.slug,
      { from: days[0], to: days[days.length - 1], duration },
      controller.signal,
    )
      .then((found) => {
        if (controller.signal.aborted) return
        setSlots(found)
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setSlotsError(asApiError(error))
      })

    return () => controller.abort()
    // `attempt` is the retry button. It carries no value; incrementing it is
    // the whole mechanism.
  }, [link, duration, month, attempt])

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

  // Derived rather than stored. A duration change can close the day that was
  // selected, and a fallback computed here cannot go stale the way a second
  // copy in state would.
  const activeDay =
    day !== null && grouped.byDay.has(day)
      ? day
      : (grouped.byDay.keys().next().value ?? null)
  const daySlots = activeDay === null ? [] : (grouped.byDay.get(activeDay) ?? [])
  const activeStart = start !== null && daySlots.includes(start) ? start : null

  const atEarliestMonth = monthIndex(month) <= monthIndex(todayIn(zone))

  function chooseDay(next: string) {
    setDay(next)
    setStart(null)
    setAnimate(true)
    setLost(null)
    // The SPA's debt. A server-rendered page would have moved focus by
    // navigating; here nothing does unless it is written.
    listRef.current?.focus()
  }

  function chooseDuration(next: number) {
    setDuration(next)
    setStart(null)
    setAnimate(true)
    setLost(null)
    // No filtering here on purpose: a longer meeting does not merely hide
    // times, it closes whole days. The effect above refetches the range.
  }

  function changeMonth(delta: number) {
    setMonth(shiftMonth(month, delta))
    setDay(null)
    setStart(null)
    setAnimate(true)
    setLost(null)
  }

  function slotLost(message: string) {
    setSlots((current) =>
      current === null ? null : current.filter((instant) => instant !== start),
    )
    setStart(null)
    setLost(message)
    setStep('choose')
  }

  const chosen =
    activeDay !== null && activeStart !== null && duration !== null
      ? {
          long: `${longDate(activeDay)}, ${zonedTime(activeStart, zone)}–${zonedTime(endOf(activeStart, duration), zone)}`,
          short: `${shortDate(zonedDate(activeStart, zone))}, ${zonedTime(activeStart, zone)}`,
        }
      : null

  const manageUrl =
    booked !== null && booked.manageToken !== null
      ? `${window.location.origin}/manage/${encodeURIComponent(booked.manageToken)}`
      : null

  function body() {
    if (linkError !== null) {
      return notice(
        'This link does not lead anywhere',
        linkError.code === NO_SUCH_LINK || linkError.status === NOT_FOUND
          ? 'Nothing is published at this address. Check the link you were sent, or ask whoever sent it for a new one.'
          : linkError.message,
      )
    }

    if (link === null || duration === null) {
      if (link !== null) {
        return notice(
          'Not taking bookings yet',
          'This link has no meeting lengths configured, so there is nothing here to book.',
        )
      }
      return notice('Loading', 'Fetching this booking link.')
    }

    // The link read succeeded and the slots read says there is no such link, so
    // it was taken down between the two. There is no day left to offer and no
    // month worth paging to.
    if (slotsError !== null && slotsError.code === NO_SUCH_LINK) {
      return notice(
        'This link stopped taking bookings',
        'It was live when this page opened and is not any more. Ask whoever sent it to you for a new one.',
      )
    }

    if (slotsError !== null && slotsError.code === CORRUPT_AVAILABILITY) {
      return notice(
        'This calendar cannot be read',
        'One of the availability rules behind this link is stored wrong, which closes every date rather than one of them. Nothing can be booked until whoever owns the link fixes it.',
      )
    }

    if (step === 'details' && activeStart !== null) {
      return (
        <BookingForm
          link={link}
          start={activeStart}
          durationMinutes={duration}
          zone={zone}
          onBack={() => setStep('choose')}
          onBooked={(booking) => {
            setBooked(booking)
            setStep('done')
          }}
          onSlotLost={slotLost}
        />
      )
    }

    if (step === 'done' && booked !== null) {
      return (
        <div className="card card-lg card-pad card-narrow">
          <span className="confirm-mark" aria-hidden="true">
            <svg
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              strokeLinecap="round"
              strokeLinejoin="round"
            >
              <path d="M5 12.5l4.5 4.5L19 7.5" />
            </svg>
          </span>

          <h1 className="title-page mt-5">You are booked</h1>
          <p className="prose mt-3">
            Confirmed for <strong>{booked.email}</strong>.
          </p>

          <div className="summary mt-5">
            <p className="summary-when numeric">
              {longDate(zonedDate(booked.start, zone))}, {zonedTime(booked.start, zone)}–
              {zonedTime(booked.end, zone)}
            </p>
            <p className="summary-meta numeric">
              {booked.durationMinutes} minutes · {zone} · {link.name}
            </p>
          </div>

          {/* The manage token exists in exactly one response, this one. The
              server keeps a hash and cannot reissue it, so a booking whose
              token is not carried into a URL here can never be moved or
              cancelled by the person who made it. */}
          {manageUrl === null ? (
            <p className="field-error mt-5" role="alert">
              <AlertIcon />
              This booking came back without its manage link, so it cannot be changed from here.
              Reply to whoever sent you this link if you need to move it.
            </p>
          ) : (
            <>
              <div className="field mt-5">
                <label htmlFor="manage-url">Your link to move or cancel this booking</label>
                <input
                  className="input"
                  id="manage-url"
                  readOnly
                  value={manageUrl}
                  onFocus={(event) => event.currentTarget.select()}
                />
                <p className="fine">
                  Save it now. It is shown on this screen and nowhere else, and the server keeps
                  only a hash of it — it cannot be sent to you again.
                </p>
              </div>
              <div className="row gap-2 wrap mt-5">
                <a className="btn" href={manageUrl}>
                  Open it
                </a>
              </div>
            </>
          )}
        </div>
      )
    }

    return (
      <div className="card card-lg booking-panes">
        <section className="card-pad">
          <h1 className="title-event mb-5">{link.name}</h1>

          <div className="facts">
            <div className="fact fact-control">
              <svg
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                strokeWidth="1.5"
                strokeLinecap="round"
                aria-hidden="true"
              >
                <circle cx="12" cy="12" r="9" />
                <path d="M12 7.5V12l3 1.8" />
              </svg>
              <DurationPicker
                durations={link.durations}
                value={duration}
                onChange={chooseDuration}
              />
            </div>
          </div>

          {link.description !== '' && (
            <p className="prose sep-top t-sm">{link.description}</p>
          )}
        </section>

        <section className="card-pad">
          <div className="month-head">
            <h2 className="title-section">Choose a time</h2>
            <div className="row gap-2">
              <button
                className="icon-btn"
                type="button"
                aria-label="Previous month"
                disabled={atEarliestMonth}
                onClick={() => changeMonth(PREVIOUS_MONTH)}
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
                onClick={() => changeMonth(NEXT_MONTH)}
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

          {/* Two columns above 861px, one below, and the month capped at 300px
              rather than taking a free fraction — all of it in kairos.css. No
              resize handler measures anything on this page. */}
          <div className="calendar-cols">
            <MonthGrid
              month={month}
              counts={grouped.counts}
              selected={activeDay}
              onSelect={chooseDay}
            />

            <div>
              {slotsError !== null ? (
                <div className="empty">
                  <p>{slotsError.message}</p>
                  <button
                    className="btn"
                    type="button"
                    onClick={() => setAttempt((current) => current + 1)}
                  >
                    Try again
                  </button>
                </div>
              ) : slots === null ? (
                <p className="prose t-sm">Finding open times…</p>
              ) : activeDay === null ? (
                <div className="empty">
                  <p>
                    Nothing in {monthLabel(month)} has a gap long enough for {duration} minutes.
                    {link.durations.length > 1 ? ' A shorter meeting may fit.' : ''}
                  </p>
                  <button
                    className="btn"
                    type="button"
                    onClick={() => changeMonth(NEXT_MONTH)}
                  >
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
                  resetKey={`${activeDay}|${duration}`}
                  onSelect={setStart}
                />
              )}

              {/* Beside the control that failed, at every width. A banner at
                  the top of the page would be nowhere near the times. */}
              {lost !== null && (
                <p className="field-error mt-4" role="alert">
                  <AlertIcon />
                  {lost} It is gone from the list — pick another time.
                </p>
              )}

              <div className="tz">
                <span className="tz-label">
                  <svg
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="1.5"
                    aria-hidden="true"
                  >
                    <circle cx="12" cy="12" r="9" />
                    <path d="M3 12h18M12 3a15 15 0 010 18 15 15 0 010-18" />
                  </svg>
                  Times shown in
                </span>
                <strong className="t-sm">{zone}</strong>
              </div>

              <div className="desktop-cta">
                <button
                  className="btn btn-primary"
                  type="button"
                  disabled={activeStart === null}
                  onClick={() => setStep('details')}
                >
                  Continue
                </button>
                <span className="muted t-sm numeric">
                  {chosen === null ? 'Pick a time to continue' : chosen.long}
                </span>
              </div>
            </div>
          </div>
        </section>
      </div>
    )
  }

  return (
    <>
      <div className="page">
        <header className="appbar">
          <span className="wordmark">
            <span className="wordmark-glyph" aria-hidden="true">
              <svg
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                strokeWidth="2.2"
                strokeLinecap="round"
              >
                <circle cx="12" cy="12" r="9" />
                <path d="M12 7.5V12l3 1.8" />
              </svg>
            </span>
            Kairos
          </span>
        </header>

        <main>{body()}</main>

        <footer className="fine page-foot">Scheduled with Kairos</footer>
      </div>

      {/* Phone only, by media query. It states the choice rather than only the
          verb, and the same button on desktop lives in .desktop-cta — the
          stylesheet shows exactly one of the two at any width. */}
      {step === 'choose' && chosen !== null && (
        <div className="actionbar">
          <div className="grow">
            <p className="actionbar-when numeric">{chosen.short}</p>
            <p className="actionbar-meta numeric">
              {duration} min · {zone}
            </p>
          </div>
          <button className="btn btn-primary" type="button" onClick={() => setStep('details')}>
            Continue
          </button>
        </div>
      )}
    </>
  )
}
