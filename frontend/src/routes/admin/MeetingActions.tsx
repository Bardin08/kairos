import { useId, useState } from 'react'

import { shortDate, zonedDate, zonedTime } from '../../lib/time'
import { cancelMeeting, withdrawProposal, type Meeting, type MeetingProposal } from './adminMeetings'
import Failure from './Failure'

// Which panel this row has open. Cancel is destructive and cannot be undone
// from here, so it goes through `confirm` first — a panel rather than a modal,
// because nothing here needs interrupting or protected focus. The same shape
// the invitee's manage page uses for the same decision.
//
// Proposing is deliberately NOT a third value. Picking a time needs a month
// grid and a column of slots, and a browser measurement showed those do not fit
// this cell at any desktop width — so `ProposePanel` swaps in over the whole
// list, the way LinkEditor does, and this component only asks for it.
type Panel = 'none' | 'confirm'

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

/** What one meeting's row lets the operator do to it. */
export default function MeetingActions({
  meetingId,
  zone,
  proposal,
  onPropose,
  onChanged,
}: {
  meetingId: string
  /** The ORGANIZER's zone. The hold below is drawn in it, never the browser's. */
  zone: string
  /** The newest hold on this meeting, or null if there has never been one. */
  proposal: MeetingProposal | null
  /** Asks the page for the time picker. The row has no width to draw one in. */
  onPropose: () => void
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
  // One row's field among many on the page, so the id cannot be written down.
  const reasonId = useId()

  function close() {
    // Cleared on the way out, not on the way in: a panel reopened later would
    // otherwise come back holding the words the operator abandoned and the
    // error from the attempt they gave up on.
    setPanel('none')
    setReason('')
    setFailure(null)
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
        <button className="btn" type="button" onClick={withdraw} data-loading={busy || undefined}>
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
        <button className="btn" type="button" onClick={onPropose}>
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
