import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, test, vi } from 'vitest'

import MeetingActions from '../src/routes/admin/MeetingActions'
import { ApiError } from '../src/lib/api'
import { cancelMeeting, withdrawProposal } from '../src/routes/admin/adminMeetings'
import type { MeetingProposal } from '../src/routes/admin/adminMeetings'

vi.mock('../src/routes/admin/adminMeetings', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/adminMeetings')>()),
  cancelMeeting: vi.fn(),
  withdrawProposal: vi.fn(),
}))

const MEETING_ID = '0192f000-0000-7000-8000-000000000001'
const REASON = 'Something came up.'
const PROPOSAL_ID = '0192f000-0000-7000-8000-000000000002'

// The ORGANIZER's, and deliberately not the zone this machine is in: the hold
// below is asserted as the organizer reads it.
const ORGANIZER_ZONE = 'Europe/Warsaw'

// September, so Warsaw is +02:00. 14:00Z is 16:00 there.
const PROPOSED_SLOT = '2030-09-09T14:00:00Z'
const PROPOSED_TIME = '16:00'
const EXPIRES_AT = '2030-09-11T14:00:00Z'
const LAPSE_DAY = /11 Sep/

const openProposal: MeetingProposal = {
  id: PROPOSAL_ID,
  start: PROPOSED_SLOT,
  expiresAt: EXPIRES_AT,
  state: 'open',
}

// The server's own sentence for the two failures that are not the same thing.
// A panel that printed one line for both would tell the operator to retry the
// one of them retrying cannot fix.
const TOO_LONG = new ApiError(400, null, 'The reason is longer than 2000 characters.')
const BAD_REQUEST_TEXT = 'The reason is longer than 2000 characters.'
const BUSY_TEXT = 'The database is busy.'
const SERVICE_UNAVAILABLE = 503
const MAX_REASON_LENGTH = 2000

/** The row's controls. Proposing is the page's job, so the row is handed a callback. */
function renderActions(proposal: MeetingProposal | null = null, onPropose = vi.fn()) {
  render(
    <MeetingActions
      meetingId={MEETING_ID}
      zone={ORGANIZER_ZONE}
      proposal={proposal}
      onPropose={onPropose}
      onChanged={vi.fn()}
    />,
  )
  return onPropose
}

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

  test('asks before cancelling and sends the reason', async () => {
    vi.mocked(cancelMeeting).mockResolvedValue(undefined as never)
    renderActions()

    fireEvent.click(screen.getByRole('button', { name: /cancel/i }))
    fireEvent.change(screen.getByLabelText(/why/i), { target: { value: REASON } })
    fireEvent.click(screen.getByRole('button', { name: /call it off/i }))

    await waitFor(() => expect(cancelMeeting).toHaveBeenCalledWith(MEETING_ID, REASON))
  })

  test('reports the failure the server actually sent, not one sentence for all of them', async () => {
    vi.mocked(cancelMeeting).mockRejectedValue(TOO_LONG)
    renderActions()

    fireEvent.click(screen.getByRole('button', { name: /cancel/i }))
    fireEvent.click(screen.getByRole('button', { name: /call it off/i }))

    const alert = await screen.findByRole('alert')
    expect(alert.textContent).toContain(BAD_REQUEST_TEXT)

    // And the panel is still open with the button still armed: cancel has no
    // second option to offer, so a failure that closed it would be a dead end.
    expect(screen.getByRole('button', { name: /call it off/i })).toBeDefined()
  })

  test('a busy server and a rejected field do not read the same', async () => {
    vi.mocked(cancelMeeting).mockRejectedValue(
      new ApiError(SERVICE_UNAVAILABLE, 'database_busy', BUSY_TEXT),
    )
    renderActions()

    fireEvent.click(screen.getByRole('button', { name: /cancel/i }))
    fireEvent.click(screen.getByRole('button', { name: /call it off/i }))

    expect((await screen.findByRole('alert')).textContent).toContain(BUSY_TEXT)
  })

  test('backing out clears the words and the error rather than keeping them for next time', async () => {
    vi.mocked(cancelMeeting).mockRejectedValue(TOO_LONG)
    renderActions()

    fireEvent.click(screen.getByRole('button', { name: /cancel/i }))
    fireEvent.change(screen.getByLabelText(/why/i), { target: { value: REASON } })
    fireEvent.click(screen.getByRole('button', { name: /call it off/i }))
    await screen.findByRole('alert')

    fireEvent.click(screen.getByRole('button', { name: /keep it/i }))
    fireEvent.click(screen.getByRole('button', { name: /cancel/i }))

    expect(screen.getByLabelText(/why/i)).toHaveProperty('value', '')
    expect(screen.queryByRole('alert')).toBeNull()
  })

  test('stops the reason at the bound the server enforces', () => {
    renderActions()

    fireEvent.click(screen.getByRole('button', { name: /cancel/i }))

    // CancelMeetingRequest.MaxReasonLength. Over it the server answers 400,
    // which is a failure the operator can do nothing about after the fact.
    expect(screen.getByLabelText(/why/i)).toHaveProperty('maxLength', MAX_REASON_LENGTH)
  })

  test('does not cancel until the panel is confirmed', () => {
    renderActions()

    fireEvent.click(screen.getByRole('button', { name: /cancel/i }))

    expect(cancelMeeting).not.toHaveBeenCalled()
  })

  test('the row asks the page for the picker rather than drawing one in the cell', () => {
    const onPropose = renderActions()

    fireEvent.click(screen.getByRole('button', { name: /suggest a new time/i }))

    // A month grid measured 8px a square inside this cell at 1280 and 3px at
    // 899. The row asks; ProposePanel draws, over the whole page.
    expect(onPropose).toHaveBeenCalled()
    expect(screen.queryByRole('group', { name: /available times/i })).toBeNull()
  })

  test('offers to withdraw an open proposal instead of proposing again', () => {
    renderActions(openProposal)

    // Only one hold may be open at a time — the server answers proposal_open —
    // so a row offering Propose over an open one is offering a 409.
    expect(screen.getByRole('button', { name: /withdraw/i })).toBeDefined()
    expect(screen.queryByRole('button', { name: /suggest a new time/i })).toBeNull()
  })

  test('the open proposal says what was suggested and when it lapses, in the organizer zone', () => {
    renderActions(openProposal)

    expect(screen.getByText(new RegExp(PROPOSED_TIME))).toBeDefined()
    expect(screen.getByText(LAPSE_DAY)).toBeDefined()
  })

  test('withdrawing names the proposal as well as the meeting', async () => {
    vi.mocked(withdrawProposal).mockResolvedValue(undefined as never)
    renderActions(openProposal)

    fireEvent.click(screen.getByRole('button', { name: /withdraw/i }))

    await waitFor(() => expect(withdrawProposal).toHaveBeenCalledWith(MEETING_ID, PROPOSAL_ID))
  })

  test('a lapsed proposal is offered again rather than offered for withdrawal', () => {
    // The server folds the clock into the state, so `expired` arrives whether
    // or not anything has swept it. Withdraw over one would ask the operator to
    // close something already closed, and would leave them no way to re-offer.
    renderActions({ ...openProposal, state: 'expired' })

    expect(screen.getByRole('button', { name: /suggest a new time/i })).toBeDefined()
    expect(screen.queryByRole('button', { name: /withdraw/i })).toBeNull()
  })
})
