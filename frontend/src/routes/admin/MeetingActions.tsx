import { useId, useState } from 'react'

import { cancelMeeting, type Meeting } from './adminMeetings'
import Failure from './Failure'

// Which panel this row has open. Cancel is destructive and cannot be undone
// from here, so it goes through `confirm` first — a panel rather than a modal,
// because nothing here needs interrupting or protected focus. The same shape
// the invitee's manage page uses for the same decision.
type Panel = 'none' | 'confirm'

// `CancelMeetingRequest.MaxReasonLength` on the server, which answers 400 above
// it. Enforced here as well so the bound is a limit the field stops at rather
// than a rejection after the fact.
const MAX_REASON = 2000

/** What one meeting's row lets the operator do to it. */
export default function MeetingActions({
  meetingId,
  onChanged,
}: {
  meetingId: string
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

  if (panel === 'none') {
    return (
      <button className="btn btn-ghost btn-danger" type="button" onClick={() => setPanel('confirm')}>
        Cancel
      </button>
    )
  }

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
