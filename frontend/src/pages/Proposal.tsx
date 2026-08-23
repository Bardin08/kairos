import { useEffect, useState, type ReactNode } from 'react'
import { useParams } from 'wouter'

import AlertIcon from '../components/AlertIcon'
import {
  acceptProposal,
  asApiError,
  declineProposal,
  getProposal,
  type Proposal as Offer,
} from '../lib/api'
import { longDate, zonedDate, zonedTime } from '../lib/time'

const NOT_FOUND = 404

// The lower-cased ProposalState the API answers with. Only `open` is a question
// still being asked; the rest are outcomes, and `outcome` below gives each its
// own sentence because they are not the same thing having happened — telling
// somebody who let a suggestion lapse that they declined it is the failure that
// separation exists to avoid.
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
      // took it back, or an accept failed its re-check and put everything back
      // — from here both are "this is no longer on offer", and neither is an
      // answer the invitee gave.
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
 * different token: this one answers one question and dies, and it cannot be
 * replayed to cancel the meeting. The path is BookingMailer.ProposalPath — the
 * mail is the only place this URL is ever built, so the two have to agree or
 * nothing is answerable.
 */
export default function Proposal() {
  const { token } = useParams()
  const [phase, setPhase] = useState<Phase>('loading')
  const [offer, setOffer] = useState<Offer | null>(null)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')

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
        // gone while it sits there intact, waiting for the answer they came to
        // give.
        if (!controller.signal.aborted) {
          setPhase(asApiError(error).status === NOT_FOUND ? 'missing' : 'broken')
        }
      })

    return () => controller.abort()
  }, [token])

  async function answer(accepting: boolean) {
    if (token === undefined || busy) return
    setBusy(true)
    setMessage('')
    try {
      if (accepting) {
        await acceptProposal(token)
        // The accept answers with the BOOKING, not the proposal — it is the
        // booking that changed. The state is set here rather than read back so
        // the page does not need a second round trip to stop offering a
        // question it has just had answered.
        setOffer((current) => (current === null ? current : { ...current, state: ACCEPTED }))
      } else {
        setOffer(await declineProposal(token))
      }
    } catch {
      // A 409 means somebody answered or the hold lapsed while this page sat
      // open, and both are answered by what a reload shows rather than by a
      // sentence guessing which. The buttons stay armed either way: nothing was
      // recorded, so a page that disarmed them would be a dead end.
      setMessage('That could not be recorded. Reload the page to see where things stand.')
    } finally {
      setBusy(false)
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

  const zone = offer.timeZone

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
        <p className="prose mt-5">{outcome(offer.state)}</p>
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

      {/* The two times, one under the other, in the organizer's zone and said
          so. Neither is a heading: they are the same fact twice, before and
          after, and a reader comparing them needs them the same size. */}
      <div className="group">
        <p className="summary-meta numeric">Now</p>
        <p className="summary-when numeric">{when(offer.currentStart, offer.currentEnd, zone)}</p>
        <p className="summary-meta numeric">Suggested</p>
        <p className="summary-when numeric">{when(offer.proposedStart, offer.proposedEnd, zone)}</p>
        <p className="summary-meta numeric">Times in {zone}</p>
      </div>

      <p className="fine mt-5">
        If nobody answers by {longDate(zonedDate(offer.expiresAt, zone))} the suggestion lapses and
        the meeting stays where it is.
      </p>

      {message !== '' && (
        <p className="field-error mt-3" role="alert">
          <AlertIcon />
          {message}
        </p>
      )}

      <div className="row gap-2 wrap mt-5">
        <button
          className="btn btn-primary"
          type="button"
          data-loading={busy || undefined}
          onClick={() => answer(true)}
        >
          Accept the new time
        </button>
        <button
          className="btn"
          type="button"
          data-loading={busy || undefined}
          onClick={() => answer(false)}
        >
          Decline
        </button>
      </div>
    </Page>
  )
}
