import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, test, vi } from 'vitest'

import MeetingActions from '../src/routes/admin/MeetingActions'
import { ApiError } from '../src/lib/api'
import { cancelMeeting, withdrawProposal } from '../src/routes/admin/adminMeetings'
import type { CancelSubject, MeetingProposal } from '../src/routes/admin/adminMeetings'

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
// 09:30Z is 11:30 in Warsaw in September — deliberately not the suggested time
// above, so an assertion on it cannot pass by matching that instead.
const EXPIRES_AT = '2030-09-11T09:30:00Z'
const EXPIRES_TIME = '11:30'
const LAPSE_DAY = /11 Sep/

// The meeting the dialog restates. Its clock times are deliberately unlike the
// proposal's below — 07:00 and 07:45 in Warsaw, not the 11:30 EXPIRES_AT holds,
// or an assertion on the lapse would have matched the restated meeting instead.
const SUBJECT = {
  participantName: 'Ada Lovelace',
  linkName: 'Consulting call',
  start: '2030-09-09T05:00:00Z',
  end: '2030-09-09T05:45:00Z',
}

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
function renderActions(
  proposal: MeetingProposal | null = null,
  onPropose = vi.fn(),
  subject: CancelSubject = SUBJECT,
) {
  render(
    <MeetingActions
      meetingId={MEETING_ID}
      zone={ORGANIZER_ZONE}
      subject={subject}
      proposal={proposal}
      onPropose={onPropose}
      onChanged={vi.fn()}
    />,
  )
  return onPropose
}

describe('MeetingActions', () => {
  // Cleared after the test rather than reset before it, unlike meetings.test.tsx:
  // mockReset drops the recorded results, and vitest reads a rejected result it no
  // longer has a record of being awaited as an unhandled rejection — so every
  // failure test here would fail on the failure it is asserting.
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
    expect(screen.queryByRole('button', { name: /call it off/i })).not.toBeNull()
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
    expect(screen.queryByRole('button', { name: /withdraw/i })).not.toBeNull()
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

  test('an open hold replaces Propose, and never the ability to call the meeting off', () => {
    renderActions(openProposal)

    // Every confirmed row could be cancelled before this feature existed. An open
    // hold is a reason not to offer a SECOND suggestion — the server answers
    // proposal_open — and no reason to make the operator withdraw one first.
    fireEvent.click(screen.getByRole('button', { name: /^cancel$/i }))

    expect(screen.queryByLabelText(/why/i)).not.toBeNull()
  })

  test('cancelling opens a modal dialog rather than growing the row', () => {
    renderActions()

    fireEvent.click(screen.getByRole('button', { name: /^cancel$/i }))

    // The row is a table cell in the narrowest column on the page. The reason
    // was typed into it until this change, and the table reflowed around it
    // every time the panel opened.
    const dialog = screen.getByRole('dialog')
    expect(dialog.tagName.toLowerCase()).toBe('dialog')
    expect(dialog).toHaveProperty('open', true)
  })

  test('the dialog names the meeting it is about, because the backdrop hides the row', () => {
    renderActions(null, vi.fn(), SUBJECT)

    fireEvent.click(screen.getByRole('button', { name: /^cancel$/i }))

    // --surface-overlay is paired with blur(8px), so the row underneath is
    // unreadable. A dialog that leaned on it for context would be leaning on
    // something the backdrop erased.
    const dialog = screen.getByRole('dialog')
    expect(dialog.textContent).toContain(SUBJECT.participantName)
    expect(dialog.textContent).toContain(SUBJECT.linkName)
  })

  test('the dialog is labelled by its own heading', () => {
    renderActions()

    fireEvent.click(screen.getByRole('button', { name: /^cancel$/i }))

    const dialog = screen.getByRole('dialog')
    const labelledBy = dialog.getAttribute('aria-labelledby')
    expect(labelledBy).not.toBeNull()
    expect(document.getElementById(labelledBy as string)?.textContent).toMatch(/call this meeting off/i)
  })

  test('focus moves to the reason rather than to the button that destroys something', () => {
    renderActions()

    fireEvent.click(screen.getByRole('button', { name: /^cancel$/i }))

    // Not the confirm: a stray Enter must never land on a destructive control
    // that was focused on the operator's behalf.
    expect(document.activeElement).toBe(screen.getByLabelText(/why/i))
  })

  test('closing returns focus to the control that opened it', () => {
    renderActions()
    const opener = screen.getByRole('button', { name: /^cancel$/i })

    fireEvent.click(opener)
    fireEvent.click(screen.getByRole('button', { name: /keep it/i }))

    expect(document.activeElement).toBe(opener)
  })

  test('a failed cancel keeps the dialog open with the words still in it', async () => {
    vi.mocked(cancelMeeting).mockRejectedValue(TOO_LONG)
    renderActions()

    fireEvent.click(screen.getByRole('button', { name: /^cancel$/i }))
    fireEvent.change(screen.getByLabelText(/why/i), { target: { value: REASON } })
    fireEvent.click(screen.getByRole('button', { name: /call it off/i }))
    await screen.findByRole('alert')

    // Cancel has no second option to offer, so a failure that dismissed the
    // dialog would be a dead end — and the reason is the operator's typing.
    expect(screen.getByRole('dialog')).toHaveProperty('open', true)
    expect(screen.getByLabelText(/why/i)).toHaveProperty('value', REASON)
  })

  test('both buttons are disabled while the cancel is in flight', async () => {
    vi.mocked(cancelMeeting).mockReturnValue(new Promise(() => {}) as never)
    renderActions()

    fireEvent.click(screen.getByRole('button', { name: /^cancel$/i }))
    fireEvent.click(screen.getByRole('button', { name: /call it off/i }))

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /call it off/i })).toHaveProperty('disabled', true)
      expect(screen.getByRole('button', { name: /keep it/i })).toHaveProperty('disabled', true)
    })
  })

  test('the destructive action carries a shape as well as a colour', () => {
    renderActions()

    fireEvent.click(screen.getByRole('button', { name: /^cancel$/i }))

    // DESIGN.md's Second Channel Rule: no state by hue alone, and this one has
    // to survive a monochrome screenshot.
    expect(screen.getByRole('button', { name: /call it off/i }).querySelector('svg')).not.toBeNull()
  })

  test('the lapse deadline on the row is a time as well as a date', () => {
    renderActions(openProposal)

    // The hold is minted-plus-hours, so it runs out at an arbitrary clock time,
    // and the invitee is reading that same instant to the minute in their mail.
    // A date alone lets the operator think they have until the end of that day.
    expect(screen.getByText(new RegExp(EXPIRES_TIME))).toBeDefined()
  })

  test('a lapsed proposal is offered again rather than offered for withdrawal', () => {
    // The server folds the clock into the state, so `expired` arrives whether
    // or not anything has swept it. Withdraw over one would ask the operator to
    // close something already closed, and would leave them no way to re-offer.
    renderActions({ ...openProposal, state: 'expired' })

    expect(screen.queryByRole('button', { name: /suggest a new time/i })).not.toBeNull()
    expect(screen.queryByRole('button', { name: /withdraw/i })).toBeNull()
  })
})
