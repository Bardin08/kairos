import { useEffect, useId, useRef, useState } from 'react'

import { longDate, shortDate, zonedDate, zonedTime } from '../../lib/time'
import {
  cancelMeeting,
  withdrawProposal,
  type CancelSubject,
  type Meeting,
  type MeetingProposal,
} from './adminMeetings'
import Failure from './Failure'

// Whether the cancel dialog is open. It is a modal, and the argument that it
// was not one — "nothing here needs interrupting or protected focus" — was
// written when this was a two-button confirm. It is now a destructive action
// carrying a free-text field a stranger reads, and it was drawn inside a table
// cell in the narrowest column on the page: the row grew by about 200px, every
// row below it moved, and the meeting being cancelled left the fold.
//
// `<dialog showModal()>` rather than a div: the focus trap, the Escape key, the
// top layer and the inertness of the page behind are the platform's, not code
// this repository would have to write and keep correct. It needs no portal
// either — the top layer paints above every z-index whatever the DOM position,
// which is why this stays inside the row's own component and its state stays
// local. jsdom implements none of that; frontend/tests/setup.ts says what the
// shim there does and does not stand for, and the browser half is pinned by
// measure-ui.mjs's `cancel-dialog` check.
//
// Proposing is deliberately NOT a third value. Picking a time needs a month grid
// and a column of slots, and a browser measurement showed those do not fit this
// cell at any desktop width — so `ProposePanel` swaps in over the whole list.
type Panel = 'none' | 'confirm'

// `CancelMeetingRequest.MaxReasonLength` on the server, which answers 400 above
// it. Enforced here as well so the bound is a limit the field stops at rather
// than a rejection after the fact.
const MAX_REASON = 2000

// The one proposal state that is still a question. Everything else has been
// answered, has lapsed or was taken back, and the row's answer to all of those
// is a fresh suggestion rather than a withdrawal. The server folds the clock
// into this word, so a hold whose expiry has passed arrives as `expired`.
const OPEN = 'open'

const DECLINED = 'declined'
const EXPIRED = 'expired'

/**
 * What became of a closed suggestion, or null when there is nothing to report.
 *
 * A refusal and a silence are two different facts about the invitee and decide
 * different things — whether the operator chases them — so one sentence for both
 * would be worse than saying nothing. `withdrawn` is silent on purpose: the
 * operator took it back themselves.
 */
function outcome(proposal: MeetingProposal | null): string | null {
  if (proposal === null) return null
  if (proposal.state === DECLINED) return 'They declined the suggested time.'
  if (proposal.state === EXPIRED) return 'A suggested time lapsed with no answer.'
  return null
}

/** What one meeting's row lets the operator do to it. */
export default function MeetingActions({
  meetingId,
  zone,
  subject,
  proposal,
  onPropose,
  onChanged,
}: {
  meetingId: string
  /** The ORGANIZER's zone. The hold below is drawn in it, never the browser's. */
  zone: string
  /** What the dialog restates. The backdrop blurs the row it belongs to. */
  subject: CancelSubject
  /** The newest hold on this meeting, or null if there has never been one. */
  proposal: MeetingProposal | null
  /** Asks the page for the time picker. The row has no width to draw one in. */
  onPropose: () => void
  onChanged: (meeting: Meeting) => void
}) {
  const [panel, setPanel] = useState<Panel>('none')
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)
  // The error itself and not a sentence made from it: `Failure` reads the status
  // and the code off it, and a 400 from the length bound, a 503 that changed
  // nothing and a dead network are three different things to do next.
  //
  // Two of them, not one. They belong to two actions with two recoveries and two
  // places to be read: a cancel fails inside the dialog, where the reason the
  // operator typed is still on screen, and a withdraw fails on the row. Sharing
  // one state printed a cancel's error in both places at once.
  const [cancelFailure, setCancelFailure] = useState<unknown>(null)
  const [withdrawFailure, setWithdrawFailure] = useState<unknown>(null)
  // One row's field among many on the page, so neither id can be written down.
  const reasonId = useId()
  const titleId = useId()
  const dialogRef = useRef<HTMLDialogElement>(null)
  // The control that opened it. Focus goes back here on close, and it is read
  // from a ref rather than from document.activeElement at close time, which by
  // then is a button inside a dialog that is on its way out.
  const openerRef = useRef<HTMLButtonElement | null>(null)

  // showModal() rather than the `open` attribute: only the method puts the
  // element in the top layer, and only the top layer gets the focus trap, the
  // inert background and the ::backdrop. React cannot express that in JSX, so
  // the effect is the seam between its state and the platform's.
  useEffect(() => {
    const dialog = dialogRef.current
    if (dialog === null) return
    if (panel === 'confirm' && !dialog.open) dialog.showModal()
    if (panel === 'none' && dialog.open) dialog.close()
  }, [panel])

  function ask(event: React.MouseEvent<HTMLButtonElement>) {
    openerRef.current = event.currentTarget
    setPanel('confirm')
  }

  function close() {
    // Cleared on the way out, not on the way in: a panel reopened later would
    // otherwise come back holding the words the operator abandoned and the
    // error from the attempt they gave up on.
    setPanel('none')
    setReason('')
    setCancelFailure(null)
    openerRef.current?.focus()
  }

  async function confirm() {
    if (busy) return
    setBusy(true)
    setCancelFailure(null)
    try {
      const cancelled = await cancelMeeting(meetingId, reason)
      close()
      onChanged(cancelled)
    } catch (error: unknown) {
      // The dialog stays open and the button stays armed. Cancel has no second
      // option to offer, so a failure that dismissed it would be a dead end —
      // and it would take the operator's typed reason with it.
      setCancelFailure(error)
    } finally {
      setBusy(false)
    }
  }

  async function withdraw() {
    if (busy || proposal === null) return
    setBusy(true)
    setWithdrawFailure(null)
    try {
      onChanged(await withdrawProposal(meetingId, proposal.id))
    } catch (error: unknown) {
      setWithdrawFailure(error)
    } finally {
      setBusy(false)
    }
  }

  // Rendered beside the row's controls rather than instead of them — the
  // confirm used to REPLACE them, which is what made the row grow — and only
  // while it is open. A closed <dialog> is display:none by the UA stylesheet,
  // so leaving it mounted would be invisible rather than wrong; it is unmounted
  // anyway, because a closed dialog left in the tree still puts the meeting's
  // restated time into every query run against the page.
  const dialog = panel !== 'confirm' ? null : (
    <dialog className="dialog" ref={dialogRef} aria-labelledby={titleId} onClose={close}>
      <div className="card card-lg card-pad">
        <div className="group">
          <h2 className="title-section" id={titleId}>
            Call this meeting off?
          </h2>
          {/* The backdrop is --surface-overlay under blur(8px), so the row this
              belongs to is unreadable behind it. Restated here to the minute
              and in the organizer's zone, the way every other time on this
              screen is drawn. */}
          <p className="record-meta numeric">
            {subject.participantName} · {longDate(zonedDate(subject.start, zone))},{' '}
            {zonedTime(subject.start, zone)}–{zonedTime(subject.end, zone)} · {subject.linkName}
          </p>
          <p className="prose t-sm">
            The slot goes back on offer and the invitee&apos;s calendar drops the event.
          </p>
          <div className="field">
            <label htmlFor={reasonId}>Why, in your words</label>
            <textarea
              className="textarea"
              id={reasonId}
              // The server's own bound, so 2100 characters is a key that does
              // not type rather than a 400 the operator is invited to retry.
              maxLength={MAX_REASON}
              value={reason}
              onChange={(event) => setReason(event.target.value)}
              // Focus goes here and not to the confirm: the operator's next act
              // is usually to type, and a stray Enter must never land on a
              // destructive button that was focused on their behalf.
              autoFocus
            />
            <span className="fine">The invitee reads this in the cancellation email.</span>
          </div>
          <Failure error={cancelFailure} />
          <div className="row gap-2 wrap">
            <button
              className="btn btn-ghost btn-danger"
              type="button"
              onClick={confirm}
              disabled={busy}
              data-loading={busy || undefined}
            >
              {/* The Second Channel Rule: the destructive action is not
                  destructive by hue alone, and an icon survives a monochrome
                  screenshot. The same mark Failure draws. */}
              <svg
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                strokeWidth="1.8"
                strokeLinecap="round"
                aria-hidden="true"
                className="icon"
              >
                <circle cx="12" cy="12" r="9" />
                <path d="M12 7.5v5M12 16h.01" />
              </svg>
              Call it off
            </button>
            <button className="btn" type="button" onClick={close} disabled={busy}>
              Keep it
            </button>
          </div>
        </div>
      </div>
    </dialog>
  )

  // A hold that is still a question. Only one may be open at a time — the
  // server answers proposal_open — so this row offers to take it back rather
  // than to make a second suggestion that would be refused.
  if (proposal !== null && proposal.state === OPEN) {
    return (
      <div className="group">
        {dialog}
        <p className="record-meta numeric">
          Suggested {shortDate(zonedDate(proposal.start, zone))}, {zonedTime(proposal.start, zone)}
        </p>
        {/* To the minute, because the hold is minted-plus-hours and lands on an
            arbitrary clock time — and because the mail the invitee is reading
            says the same instant to the minute. */}
        <p className="record-meta numeric">
          Lapses {shortDate(zonedDate(proposal.expiresAt, zone))},{' '}
          {zonedTime(proposal.expiresAt, zone)}
        </p>
        <Failure error={withdrawFailure} />
        <div className="row gap-2 wrap">
          <button className="btn" type="button" onClick={withdraw} data-loading={busy || undefined}>
            Withdraw
          </button>
          {/* Cancel stays. An open hold replaces PROPOSE, never the ability to
              call the meeting off — a branch that returned early without it made
              the operator withdraw a suggestion first, with nothing on the row
              saying so. */}
          <button className="btn btn-ghost btn-danger" type="button" onClick={ask}>
            Cancel
          </button>
        </div>
      </div>
    )
  }

  const news = outcome(proposal)

  return (
    <div className="group">
      {dialog}
      {/* What became of the last suggestion, next to the button that makes
          another one. It is here rather than beside the meeting's own status
          because that status did not change: a refused or lapsed suggestion
          leaves a confirmed meeting exactly where it was. */}
      {news !== null && <p className="record-meta">{news}</p>}
      <Failure error={withdrawFailure} />
      <div className="row gap-2 wrap">
        <button className="btn" type="button" onClick={onPropose}>
          Suggest a new time
        </button>
        <button className="btn btn-ghost btn-danger" type="button" onClick={ask}>
          Cancel
        </button>
      </div>
    </div>
  )
}
