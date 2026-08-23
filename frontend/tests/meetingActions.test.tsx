import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import MeetingActions from '../src/routes/admin/MeetingActions'
import { cancelMeeting } from '../src/routes/admin/adminMeetings'

vi.mock('../src/routes/admin/adminMeetings', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/adminMeetings')>()),
  cancelMeeting: vi.fn(),
}))

const MEETING_ID = '0192f000-0000-7000-8000-000000000001'
const REASON = 'Something came up.'

describe('MeetingActions', () => {
  beforeEach(() => vi.mocked(cancelMeeting).mockReset())

  afterEach(cleanup)

  it('asks before cancelling and sends the reason', async () => {
    vi.mocked(cancelMeeting).mockResolvedValue(undefined as never)
    render(<MeetingActions meetingId={MEETING_ID} onChanged={vi.fn()} />)

    fireEvent.click(screen.getByRole('button', { name: /cancel/i }))
    fireEvent.change(screen.getByLabelText(/why/i), { target: { value: REASON } })
    fireEvent.click(screen.getByRole('button', { name: /call it off/i }))

    await waitFor(() => expect(cancelMeeting).toHaveBeenCalledWith(MEETING_ID, REASON))
  })

  it('does not cancel until the panel is confirmed', () => {
    render(<MeetingActions meetingId={MEETING_ID} onChanged={vi.fn()} />)

    fireEvent.click(screen.getByRole('button', { name: /cancel/i }))

    expect(cancelMeeting).not.toHaveBeenCalled()
  })
})
