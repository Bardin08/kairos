import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import MeetingActions from '../src/routes/admin/MeetingActions'
import { ApiError } from '../src/lib/api'
import { cancelMeeting } from '../src/routes/admin/adminMeetings'

vi.mock('../src/routes/admin/adminMeetings', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/adminMeetings')>()),
  cancelMeeting: vi.fn(),
}))

const MEETING_ID = '0192f000-0000-7000-8000-000000000001'
const REASON = 'Something came up.'

// The server's own sentence for the two failures that are not the same thing.
// A panel that printed one line for both would tell the operator to retry the
// one of them retrying cannot fix.
const TOO_LONG = new ApiError(400, null, 'The reason is longer than 2000 characters.')
const BAD_REQUEST_TEXT = 'The reason is longer than 2000 characters.'
const BUSY_TEXT = 'The database is busy.'

describe('MeetingActions', () => {
  // Cleared after the test rather than reset before it, which is what
  // meetings.test.tsx does and is not a stylistic choice: mockReset drops the
  // recorded results, and vitest reads a rejected result it no longer has a
  // record of being awaited as an unhandled rejection — so every test here that
  // exercises a failure would fail on the failure it is asserting.
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('asks before cancelling and sends the reason', async () => {
    vi.mocked(cancelMeeting).mockResolvedValue(undefined as never)
    render(<MeetingActions meetingId={MEETING_ID} onChanged={vi.fn()} />)

    fireEvent.click(screen.getByRole('button', { name: /cancel/i }))
    fireEvent.change(screen.getByLabelText(/why/i), { target: { value: REASON } })
    fireEvent.click(screen.getByRole('button', { name: /call it off/i }))

    await waitFor(() => expect(cancelMeeting).toHaveBeenCalledWith(MEETING_ID, REASON))
  })

  it('reports the failure the server actually sent, not one sentence for all of them', async () => {
    vi.mocked(cancelMeeting).mockRejectedValue(TOO_LONG)
    render(<MeetingActions meetingId={MEETING_ID} onChanged={vi.fn()} />)

    fireEvent.click(screen.getByRole('button', { name: /cancel/i }))
    fireEvent.click(screen.getByRole('button', { name: /call it off/i }))

    const alert = await screen.findByRole('alert')
    expect(alert.textContent).toContain(BAD_REQUEST_TEXT)

    // And the panel is still open with the button still armed: cancel has no
    // second option to offer, so a failure that closed it would be a dead end.
    expect(screen.getByRole('button', { name: /call it off/i })).toBeDefined()
  })

  it('a busy server and a rejected field do not read the same', async () => {
    vi.mocked(cancelMeeting).mockRejectedValue(new ApiError(503, 'database_busy', BUSY_TEXT))
    render(<MeetingActions meetingId={MEETING_ID} onChanged={vi.fn()} />)

    fireEvent.click(screen.getByRole('button', { name: /cancel/i }))
    fireEvent.click(screen.getByRole('button', { name: /call it off/i }))

    expect((await screen.findByRole('alert')).textContent).toContain(BUSY_TEXT)
  })

  it('backing out clears the words and the error rather than keeping them for next time', async () => {
    vi.mocked(cancelMeeting).mockRejectedValue(TOO_LONG)
    render(<MeetingActions meetingId={MEETING_ID} onChanged={vi.fn()} />)

    fireEvent.click(screen.getByRole('button', { name: /cancel/i }))
    fireEvent.change(screen.getByLabelText(/why/i), { target: { value: REASON } })
    fireEvent.click(screen.getByRole('button', { name: /call it off/i }))
    await screen.findByRole('alert')

    fireEvent.click(screen.getByRole('button', { name: /keep it/i }))
    fireEvent.click(screen.getByRole('button', { name: /cancel/i }))

    expect(screen.getByLabelText(/why/i)).toHaveProperty('value', '')
    expect(screen.queryByRole('alert')).toBeNull()
  })

  it('stops the reason at the bound the server enforces', () => {
    render(<MeetingActions meetingId={MEETING_ID} onChanged={vi.fn()} />)

    fireEvent.click(screen.getByRole('button', { name: /cancel/i }))

    // 2000 is CancelMeetingRequest.MaxReasonLength. Over it the server answers
    // 400, which is a failure the operator can do nothing about after the fact.
    expect(screen.getByLabelText(/why/i)).toHaveProperty('maxLength', 2000)
  })

  it('does not cancel until the panel is confirmed', () => {
    render(<MeetingActions meetingId={MEETING_ID} onChanged={vi.fn()} />)

    fireEvent.click(screen.getByRole('button', { name: /cancel/i }))

    expect(cancelMeeting).not.toHaveBeenCalled()
  })
})
