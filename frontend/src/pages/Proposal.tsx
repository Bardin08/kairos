import { useEffect, useRef, useState, type ReactNode } from 'react'
import { useParams } from 'wouter'

import AlertIcon from '../components/AlertIcon'
import {
  acceptProposal,
  asApiError,
  declineProposal,
  getProposal,
  type Proposal as Offer,
} from '../lib/api'
import { localZone, longDate, zonedDate, zonedTime } from '../lib/time'

const NOT_FOUND = 404

// The lower-cased ProposalState the API answers with. Only `open` is a question
// still being asked; the rest are outcomes, and `outcome` below gives each its
// own sentence — telling somebody who let a suggestion lapse that they declined
// it is the failure that separation exists to avoid.
const OPEN = 'open'
const ACCEPTED = 'accepted'
const DECLINED = 'declined'
const EXPIRED = 'expired'

// Which whole-page answer the route is giving. `missing` is the one honest
// reply to any token that matches nothing — a real token for a deleted proposal
// and a truncated one get the same page, because telling them apart tells a
// guesser which half of the token was right.
type Phase = 'loading' | 'ready' | 'missing' | 'broken'

/** `Monday 9 September, 20:00–20:30` in the given zone. */
function when(start: string, end: string, zone: string): string {
  return `${longDate(zonedDate(start, zone))}, ${zonedTime(start, zone)}–${zonedTime(end, zone)}`
}

/**
 * `Monday 9 September, 14:00` — the moment the hold runs out, to the minute.
 *
 * The date alone would be a lie by omission. `ExpiresAt` is the mint time plus
 * the hold in hours, so it lands on whatever clock time the operator pressed the
 * button at, and the mail prints both halves. "By Monday 9 September" read at
 * 18:00 says an offer that died at 14:00 is still open.
 */
function moment(instant: string, zone: string): string {
  return `${longDate(zonedDate(instant, zone))}, ${zonedTime(instant, zone)}`
}

/** What is true now that the suggestion is closed, in the invitee's terms. */
function outcome(state: string): string {
  switch (state) {
    case ACCEPTED:
      return 'The meeting has been moved to the new time. Your calendar invitation updates itself; nothing else needs doing.'
    case DECLINED:
      return 'You declined the new time, so the meeting stays where it is.'
    case EXPIRED:
      return 'Nobody answered in time, so this suggestion lapsed and the meeting stays where it is.'
    default:
      // `withdrawn`, and anything a later version of the API adds. The operator
      // took it back, or an accept failed its re-check — from here both are
      // "this is no longer on offer", and neither is an answer the invitee gave.
      return 'This suggestion is no longer on offer, so the meeting stays where it is.'
  }
}

function Page({ children }: { children: ReactNode }) {
  return (
    <div className="page">
      <main className="card card-lg card-pad card-narrow mt-6">{children}</main>
      <footer className="fine page-foot">Scheduled with Kairos</footer>
    </div>
  )
}

/**
 * The page the invitee opens from "a new time is suggested".
 *
 * Its own route rather than a panel on the manage page, because the token is a
 * different token: this one answers one question and dies, and cannot be
 * replayed to cancel the meeting. The path is BookingMailer.ProposalPath — the
 * mail is the only place this URL is built, so the two have to agree.
 */
export default function Proposal() {
  const { token } = useParams()
  const [phase, setPhase] = useState<Phase>('loading')
  const [offer, setOffer] = useState<Offer | null>(null)
  // WHICH answer is in flight, not merely that one is. `data-loading` blanks a
  // button's label and takes its pointer events, so marking both would leave the
  // invitee looking at two pending actions with no way to tell them apart.
  const [running, setRunning] = useState<'accept' | 'decline' | null>(null)
  const [message, setMessage] = useState('')
  // Focus lands here when an answer replaces the question: on a server-rendered
  // page the navigation would have moved it, and in an SPA nothing does unless
  // it is written. Only for an answer given HERE — a page that loads
  // already-declined must not steal focus from a reader who has just arrived.
  const outcomeRef = useRef<HTMLParagraphElement>(null)
  const answeredHere = useRef(false)

  useEffect(() => {
    if (token === undefined) {
      setPhase('missing')
      return
    }
    const controller = new AbortController()

    getProposal(token, controller.signal)
      .then((answer) => {
        setOffer(answer)
        setPhase('ready')
      })
      .catch((error: unknown) => {
        // Only a 404 is "this link leads nowhere". A 500 or a dropped
        // connection rendered as one would tell an invitee the suggestion is
        // gone while it sits there intact.
        if (!controller.signal.aborted) {
          setPhase(asApiError(error).status === NOT_FOUND ? 'missing' : 'broken')
        }
      })

    return () => controller.abort()
  }, [token])

  useEffect(() => {
    if (answeredHere.current) outcomeRef.current?.focus()
  }, [offer?.state])

  async function answer(accepting: boolean) {
    if (token === undefined || running !== null) return
    setRunning(accepting ? 'accept' : 'decline')
    setMessage('')
    try {
      if (accepting) {
        await acceptProposal(token)
        // The accept answers with the BOOKING, not the proposal — it is the
        // booking that changed. The state is set here rather than read back, so
        // the page needs no second round trip to stop offering the question.
        setOffer((current) => (current === null ? current : { ...current, state: ACCEPTED }))
      } else {
        setOffer(await declineProposal(token))
      }
      answeredHere.current = true
    } catch {
      // A 409 means somebody answered or the hold lapsed while this page sat
      // open, and both are answered by what a reload shows. The buttons stay
      // armed either way: nothing was recorded, so disarming them is a dead end.
      setMessage('That could not be recorded. Reload the page to see where things stand.')
    } finally {
      setRunning(null)
    }
  }

  if (phase === 'loading') {
    return (
      <Page>
        <p className="prose t-sm">Reading the suggestion…</p>
      </Page>
    )
  }

  if (phase === 'broken') {
    return (
      <Page>
        <h1 className="title-page">Something went wrong</h1>
        <p className="prose mt-3">
          The server is not answering as it should. Reload in a minute; if it keeps happening, the
          organizer is the one who can fix it.
        </p>
      </Page>
    )
  }

  if (phase === 'missing' || offer === null) {
    return (
      <Page>
        <h1 className="title-page">This link does not lead anywhere</h1>
        <p className="prose mt-3">
          The suggestion may already have been taken back, or the link may have been cut short when
          it was copied. Ask whoever sent it for a fresh one.
        </p>
      </Page>
    )
  }

  // The INVITEE's own zone, the one the proposal mail formatted against. Empty
  // only for a booking carrying no booker at all, and then the browser's rather
  // than the organizer's — `Intl` throws a RangeError on an empty zone, and here
  // that would happen during render and take the whole page down over one row.
  const zone = offer.timeZone === '' ? localZone() : offer.timeZone

  if (offer.state !== OPEN) {
    return (
      <Page>
        <h1 className="title-event mb-4">{offer.linkName}</h1>
        <div className="group">
          <p className="summary-when numeric">
            {offer.state === ACCEPTED
              ? when(offer.proposedStart, offer.proposedEnd, zone)
              : when(offer.currentStart, offer.currentEnd, zone)}
          </p>
          <p className="summary-meta numeric">Times in {zone}</p>
        </div>
        {/* Announced as well as focused: the question this replaced is gone from
            the document, and a reader who cannot see that needs telling what
            took its place. */}
        <p className="prose mt-5" role="status" tabIndex={-1} ref={outcomeRef}>
          {outcome(offer.state)}
        </p>
      </Page>
    )
  }

  return (
    <Page>
      <h1 className="title-event mb-4">{offer.linkName}</h1>
      <p className="prose mb-5">
        The organizer would like to move this meeting. Nothing changes unless you accept, and
        declining leaves it exactly where it is.
      </p>

      {/* The two times, one under the other, in the invitee's own zone and said
          so. Neither is a heading: they are the same fact twice, before and
          after, and a reader comparing them needs them the same size. */}
      <div className="group">
        <p className="summary-meta numeric">Now</p>
        <p className="summary-when numeric">{when(offer.currentStart, offer.currentEnd, zone)}</p>
        <p className="summary-meta numeric">Suggested</p>
        <p className="summary-when numeric">{when(offer.proposedStart, offer.proposedEnd, zone)}</p>
        <p className="summary-meta numeric">Times in {zone}</p>
      </div>

      <p className="fine mt-5 numeric">
        If nobody answers by {moment(offer.expiresAt, zone)} the suggestion lapses and the meeting
        stays where it is.
      </p>

      {message !== '' && (
        <p className="field-error mt-3" role="alert">
          <AlertIcon />
          {message}
        </p>
      )}

      <div className="row gap-2 wrap mt-5">
        {/* `disabled` as well as `data-loading`, and they are not the same guard.
            The attribute is a look — the stylesheet blanks the label and drops
            pointer events — which a keyboard still fires Space through.
            `disabled` is the part that actually stops the second answer. */}
        <button
          className="btn btn-primary"
          type="button"
          data-loading={running === 'accept' || undefined}
          disabled={running !== null}
          onClick={() => answer(true)}
        >
          Accept the new time
        </button>
        <button
          className="btn"
          type="button"
          data-loading={running === 'decline' || undefined}
          disabled={running !== null}
          onClick={() => answer(false)}
        >
          Decline
        </button>
      </div>
    </Page>
  )
}
