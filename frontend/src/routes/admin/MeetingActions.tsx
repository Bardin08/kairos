import { useId, useState } from 'react'

import { cancelMeeting } from './adminMeetings'

// Which panel this row has open. Cancel is destructive and cannot be undone
// from here, so it goes through `confirm` first — a panel rather than a modal,
// because nothing here needs interrupting or protected focus. The same shape
// the invitee's manage page uses for the same decision.
type Panel = 'none' | 'confirm'

/** What one meeting's row lets the operator do to it. */
export default function MeetingActions({
  meetingId,
  onChanged,
}: {
  meetingId: string
  onChanged: () => void
}) {
  const [panel, setPanel] = useState<Panel>('none')
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)
  const [failed, setFailed] = useState(false)
  // One row's field among many on the page, so the id cannot be written down.
  const reasonId = useId()

  async function confirm() {
    if (busy) return
    setBusy(true)
    setFailed(false)
    try {
      await cancelMeeting(meetingId, reason)
      setPanel('none')
      onChanged()
    } catch {
      setFailed(true)
    } finally {
      setBusy(false)
    }
  }

  if (panel === 'none') {
    return (
      <button className="btn" type="button" onClick={() => setPanel('confirm')}>
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
          value={reason}
          onChange={(event) => setReason(event.target.value)}
        />
        <span className="fine">The invitee reads this in the cancellation.</span>
      </div>
      {failed && <p className="fine">That did not go through. Try again.</p>}
      <div className="row gap-2">
        <button
          className="btn btn-primary"
          type="button"
          onClick={confirm}
          data-loading={busy || undefined}
        >
          Call it off
        </button>
        <button className="btn" type="button" onClick={() => setPanel('none')}>
          Keep it
        </button>
      </div>
    </div>
  )
}
