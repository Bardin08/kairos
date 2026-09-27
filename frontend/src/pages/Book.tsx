import { useEffect, useMemo, useRef, useState } from 'react'
import { useLocation, useParams } from 'wouter'

import AlertIcon from '../components/AlertIcon'
import BrandMark from '../components/BrandMark'
import BookingForm from '../components/BookingForm'
import DurationPicker from '../components/DurationPicker'
import MonthGrid from '../components/MonthGrid'
import SlotList from '../components/SlotList'
import {
  asApiError,
  getInvite,
  getLink,
  getSlots,
  type ApiError,
  type Booking,
  type Invite,
  type InviteState,
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
import { applyPrefill, FILLABLE, type Details } from './bookPrefill'

// One corrupt stored availability rule kills every range containing that
// weekday, so there is no day to mark and no month to page to. It is the whole
// page's problem, not the slot column's.
const CORRUPT_AVAILABILITY = 'pattern_invalid'

// Both link routes answer this for a slug that resolves to nothing — including
// a slug no link could ever have. Keyed on the code and never on the title:
// the title is prose the server may reword at any release, the code is the
// contract it keeps.
const NO_SUCH_LINK = 'not_found'

// The one invite state that fills a form in, and the only one that is still
// worth spending. The other three are stale rather than broken.
const INVITE_OPEN = 'open'

// The query key an invite URL carries its token on, matching what the operator's
// Copy button builds — InvitesAdminEndpoints.TokenQueryKey.
const INVITE_TOKEN_KEY = 'i'

// The one state that names a date. "Expired" on its own leaves the reader unable
// to tell whether they missed it by an hour or by a month.
const INVITE_EXPIRED = 'expired'

// One clause per state that closed the invitation, because "this did not work"
// is not something a reader can do anything with. What follows the clause is the
// same for all three, so it is written once below rather than three times here.
const STALE_INVITE: Record<Exclude<InviteState, typeof INVITE_OPEN>, string> = {
  consumed: 'has already been used',
  revoked: 'was withdrawn',
  expired: 'expired',
}

const NEXT_MONTH = 1
const PREVIOUS_MONTH = -1
const NOT_FOUND = 404

type Step = 'choose' | 'details' | 'done'

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
 * Two zones are in play and mixing them is the bug worth naming once: the month
 * grid is keyed on the ORGANIZER's calendar, because that is what `from` and
 * `to` mean to the slots endpoint, while every time on screen is the INVITEE's.
 * The instant itself is never re-derived.
 */
export default function Book() {
  const { slug = '' } = useParams()
  const [, navigate] = useLocation()

  // Read once, at mount. A zone that changed mid-booking would silently move
  // every time on screen out from under a selection already made.
  const [zone] = useState(localZone)

  // Read once too, and off `window` rather than the router: wouter parses the
  // path, and an invite arrives in the query string, which it does not.
  const [token] = useState(
    () => new URLSearchParams(window.location.search).get(INVITE_TOKEN_KEY),
  )
  const [invite, setInvite] = useState<Invite | null>(null)

  const [link, setLink] = useState<Link | null>(null)
  const [linkError, setLinkError] = useState<ApiError | null>(null)

  // Null until the link resolves, and deliberately not seeded from the invitee's
  // calendar. The grid is keyed on the ORGANIZER's dates, so opening on the
  // invitee's month puts the two out of step by a day across the date line: an
  // invitee in Tokyo at 00:30 on 1 September would open on September with
  // Previous disabled, and the organizer's 31 August could never be reached.
  const [month, setMonth] = useState<string | null>(null)
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

  // Lives here, not in the form. A 409 sends the invitee back to the times to
  // choose again, which unmounts the form — and losing a typed name and email to
  // a race they did not cause is a worse outcome than the lost slot.
  const [details, setDetails] = useState<Details>({ name: '', email: '', notes: '' })

  // Which fields the invitee has been in. A ref rather than state because
  // nothing on screen changes when a field is first touched, and because the
  // invite read below has to see the set as it stands the moment it resolves
  // rather than one captured by the render that started the request.
  const touched = useRef(new Set<string>())

  const listRef = useRef<HTMLElement>(null)
  const wantFocus = useRef(false)

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
        // The organizer's calendar, not the invitee's. This is the first moment
        // that zone is known, which is why the month is not seeded above.
        setMonth(startOfMonth(todayIn(loaded.timeZone)))
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
    if (token === null) return

    const controller = new AbortController()

    getInvite(token, controller.signal)
      .then((found) => {
        if (controller.signal.aborted) return
        setInvite(found)
        // Seeded here rather than from a second effect watching `invite`, which
        // is what makes "exactly once" a property of where the code sits rather
        // than of a flag guarding it: this runs when the read lands, and the
        // read happens once. `touched` is consulted at that moment, so a field
        // the invitee typed into while the request was in flight keeps what they
        // typed — the race this whole one-shot exists for.
        if (found.state === INVITE_OPEN) {
          setDetails((current) => applyPrefill(current, found.prefill, touched.current))
        }
      })
      .catch(() => {
        // A token nobody issued is not an error this page reports, and neither
        // is a read that never arrived. There is still a link, still a month of
        // times, and the ordinary booking form is exactly what a null invite
        // draws. The 404 is deliberately indistinguishable from the rest here:
        // telling a guesser that a token was merely wrong is a question about a
        // row they have no business reading.
      })

    return () => controller.abort()
  }, [token])

  useEffect(() => {
    if (link === null || duration === null || month === null) return

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

  // Derived rather than stored: a duration change can close the day that was
  // selected, and a fallback computed here cannot go stale the way a second copy
  // in state would. Smallest key rather than the first inserted — date keys sort
  // lexicographically, so this holds even if the endpoint stops returning
  // instants in ascending order.
  const earliestDay = [...grouped.byDay.keys()].reduce<string | null>(
    (found, key) => (found === null || key < found ? key : found),
    null,
  )
  const activeDay = day !== null && grouped.byDay.has(day) ? day : earliestDay
  const daySlots = activeDay === null ? [] : (grouped.byDay.get(activeDay) ?? [])
  const activeStart = start !== null && daySlots.includes(start) ? start : null

  // The organizer's today, for the same reason the month is. Measured against
  // the invitee's, an invitee ahead of the organizer loses the organizer's
  // current day and one behind can page back into a month already spent.
  const atEarliestMonth =
    link === null ||
    month === null ||
    monthIndex(month) <= monthIndex(todayIn(link.timeZone))

  function chooseDay(next: string) {
    setDay(next)
    setStart(null)
    setAnimate(true)
    setLost(null)
    // Asking for a day is the only thing that earns the focus move. Recorded
    // here and spent in the effect below.
    wantFocus.current = true
  }

  // The SPA's debt: a server-rendered page would have moved focus by navigating.
  // It runs after the commit rather than in the click handler, because before
  // the commit the section still carries the previous day's accessible name.
  //
  // Gated on intent rather than on `activeDay`, which is DERIVED: it moves when
  // the month pages and when the duration closes the selected day, neither of
  // which is a request to read the times. Keyed on the value, paging forward
  // three months threw focus out of the month arrow three times. No dependency
  // array, so the flag is what triggers it and not a value.
  useEffect(() => {
    if (!wantFocus.current) return
    wantFocus.current = false
    // Null while the whole month is empty. The intent is spent either way,
    // rather than banked and fired at some later unrelated render.
    listRef.current?.focus()
  })

  function chooseDuration(next: number) {
    setDuration(next)
    setStart(null)
    setAnimate(true)
    setLost(null)
    // No filtering here on purpose: a longer meeting does not merely hide
    // times, it closes whole days. The effect above refetches the range.
  }

  function changeMonth(delta: number) {
    setMonth((current) => (current === null ? null : shiftMonth(current, delta)))
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

  // The form hands back the whole object rather than the field that moved, so
  // what the invitee has been in is worked out by comparing the two. Marked on
  // the first keystroke and never unmarked: clearing a field is a decision the
  // operator's guess does not get to overrule either.
  function changeDetails(next: Details) {
    for (const field of FILLABLE) {
      if (next[field] !== details[field]) touched.current.add(field)
    }
    setDetails(next)
  }

  // In the INVITEE's zone, like every other time this page draws: the organizer's
  // calendar is not something the reader of this line holds. Null when the invite
  // never lapses, which is the ordinary invite.
  const deadline =
    invite === null || invite.expiresAt === null
      ? null
      : `${longDate(zonedDate(invite.expiresAt, zone))}, ${zonedTime(invite.expiresAt, zone)}`

  const expiredOn =
    invite !== null && invite.state === INVITE_EXPIRED && deadline !== null
      ? ` on ${deadline}`
      : ''

  const staleInvite =
    invite === null || invite.state === INVITE_OPEN
      ? null
      : `This personal invitation ${STALE_INVITE[invite.state]}${expiredOn}, so nothing was filled in for you. Every time below is still open to book.`

  // The other end of the same field, and why the API answers it whatever the
  // state: a time-limited offer whose limit the invitee cannot see is not one.
  const goodUntil = invite !== null && invite.state === INVITE_OPEN ? deadline : null

  // Dropped only where the read POSITIVELY reported a state other than open: the
  // write refuses that token, so sending it buys an alarming sentence and nothing
  // else. A read that FAILED reports nothing — a blip, a timeout, a rate limit on
  // a reloaded page — and dropping the token there would book the meeting and
  // leave the operator's single-use link still spendable, which is the one thing
  // this feature exists to hold. So it still goes; if it does turn out to be
  // spent, BookingForm's own refusal path clears it and the next press books
  // without it.
  const spendable = invite !== null && invite.state !== INVITE_OPEN ? null : token

  const chosen =
    activeDay !== null && activeStart !== null && duration !== null
      ? {
          // Both halves in the INVITEE's calendar. Naming the organizer's day
          // beside the invitee's clock is how a desktop reader was told
          // "Monday 7 September, 01:00" for a slot the next screen, the phone
          // bar and the slot's own `+1d` all called Tuesday the 8th.
          long: `${longDate(zonedDate(activeStart, zone))}, ${zonedTime(activeStart, zone)}–${zonedTime(endOf(activeStart, duration), zone)}`,
          short: `${shortDate(zonedDate(activeStart, zone))}, ${zonedTime(activeStart, zone)}`,
        }
      : null

  // The path is what the router navigates to; the absolute URL is what the
  // invitee keeps. Both come off the one token this API will ever hand back.
  const managePath =
    booked !== null && booked.manageToken !== null
      ? `/manage/${encodeURIComponent(booked.manageToken)}`
      : null

  function body() {
    if (linkError !== null) {
      // Only a genuine 404 gets the "does not lead anywhere" heading. A link
      // that exists but cannot be used — a network failure, an unreadable
      // organizer zone — is a different sentence.
      const missing = linkError.code === NO_SUCH_LINK || linkError.status === NOT_FOUND
      return notice(
        missing ? 'This link does not lead anywhere' : 'This link cannot be opened',
        missing
          ? 'Nothing is published at this address. Check the link you were sent, or ask whoever sent it for a new one.'
          : linkError.message,
      )
    }

    if (link === null || duration === null || month === null) {
      if (link !== null && duration === null) {
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
          details={details}
          inviteToken={spendable}
          onDetails={changeDetails}
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
              server keeps a hash and cannot reissue it, so a booking whose token
              is not carried into a URL here can never be moved or cancelled. */}
          {managePath === null ? (
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
                  value={window.location.origin + managePath}
                  onFocus={(event) => event.currentTarget.select()}
                />
                <p className="fine">
                  Save it now. It is shown on this screen and nowhere else, and the server keeps only a hash of it — it cannot be sent to you again.
                </p>
              </div>
              <div className="row gap-2 wrap mt-5">
                {/* A button rather than an anchor: kairos.css gives every <a> a
                    text-decoration-thickness and resets it only for .wordmark,
                    .nav a and .tabbar a, so `.btn` on an anchor renders
                    underlined. The manage page reached the same conclusion. */}
                <button
                  className="btn"
                  type="button"
                  onClick={() => navigate(managePath)}
                >
                  Open it
                </button>
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
              {/* THE FRAME, AND IT WRAPS THE FOUR BRANCHES AND NOTHING ELSE.
                  Three of them never render SlotList and the one that does is
                  unmounted while a month is being fetched, so a floor on
                  `.slots` inside SlotList applies exactly when it is not needed.
                  It does not belong on `.slot-panel` either: that also holds the
                  zone line and the desktop CTA, so flooring it lets those two
                  keep their 83px while the states area is squeezed by the same
                  amount. Measured on the deployed page with the floor there:
                  the panel held its 332px and still went 409 -> 332 -> 409
                  across a month change. */}
              <div className="slot-states">
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
                  <p className="numeric">
                    Nothing in {monthLabel(month)} has a gap long enough for {duration} minutes.
                    {link.durations.length > 1 ? ' A shorter meeting may fit.' : ''}
                  </p>
                  <button
                    className="btn numeric"
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

              </div>

              {/* Outside the frame: this reports a booking that was refused,
                  not a month that is loading, and an error is allowed to make
                  the panel taller.

                  Beside the control that failed, at every width. A banner at
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
            <BrandMark />
            Kairos
          </span>
        </header>

        <main>
          {/* The region is in the document from the first render even when it
              holds nothing. A live region inserted at the same moment as its
              text is announced unevenly across screen readers; one that was
              already there announces every time. Polite and never assertive:
              the times are all still bookable, so there is nothing here worth
              interrupting somebody mid-sentence for. */}
          <div role="status" aria-live="polite">
            {step !== 'done' && staleInvite !== null && (
              // `.empty` is the stylesheet's own reason-and-recovery box —
              // sunken, dashed, no tint — and it is already what this page says
              // a closed month in. Nothing in kairos.css needed adding.
              <div className="empty mb-5">
                <p className="numeric">{staleInvite}</p>
              </div>
            )}

            {step !== 'done' && goodUntil !== null && (
              <p className="prose numeric mb-5">
                This personal invitation is good until {goodUntil}, in {zone}.
              </p>
            )}
          </div>

          {body()}
        </main>

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
