import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, test, vi } from 'vitest'

import MeetingActions from '../src/routes/admin/MeetingActions'
import { ApiError, getSlots } from '../src/lib/api'
import { cancelMeeting, proposeMeeting, withdrawProposal } from '../src/routes/admin/adminMeetings'
import type { MeetingProposal } from '../src/routes/admin/adminMeetings'

vi.mock('../src/routes/admin/adminMeetings', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/adminMeetings')>()),
  cancelMeeting: vi.fn(),
  proposeMeeting: vi.fn(),
  withdrawProposal: vi.fn(),
}))

// The slot read is the PUBLIC one, in lib/api rather than beside the admin
// calls: an operator picking a time and an invitee picking one are the same act
// against the same generator, and a second endpoint for the operator would be a
// second answer to what a link offers.
vi.mock('../src/lib/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/lib/api')>()),
  getSlots: vi.fn(),
}))

// Same file the running app loads, and the authority on which class names exist.
const stylesheet = resolve(process.cwd(), '../backend/src/Kairos.Api/wwwroot/kairos.css')

const MEETING_ID = '0192f000-0000-7000-8000-000000000001'
const REASON = 'Something came up.'
const SLUG = 'consulting'
const DURATION_MINUTES = 30

// The ORGANIZER's, and deliberately not the zone this machine is in: the
// suggested time below is asserted as the organizer reads it.
const ORGANIZER_ZONE = 'Europe/Warsaw'

// September, so Warsaw is +02:00. Every date here is inside the month the
// picker opens on, which is the month containing TODAY below.
const TODAY = '2030-09-01T08:00:00Z'
const PROPOSED_SLOT = '2030-09-09T14:00:00Z'
const PROPOSED_TIME = '16:00'
const OTHER_SLOT = '2030-09-09T15:00:00Z'
const EXPIRES_AT = '2030-09-11T14:00:00Z'
const LAPSE_DAY = /11 Sep/
const PROPOSAL_ID = '0192f000-0000-7000-8000-000000000002'

const openProposal: MeetingProposal = {
  id: PROPOSAL_ID,
  start: PROPOSED_SLOT,
  expiresAt: EXPIRES_AT,
  state: 'open',
}

/**
 * The row's actions, with the two facts the propose panel cannot work without:
 * the slug the slots are read from, and the length the suggestion has to keep.
 */
function renderActions(proposal: MeetingProposal | null = null) {
  render(
    <MeetingActions
      meetingId={MEETING_ID}
      slug={SLUG}
      durationMinutes={DURATION_MINUTES}
      zone={ORGANIZER_ZONE}
      proposal={proposal}
      onChanged={vi.fn()}
    />,
  )
}

// The server's own sentence for the two failures that are not the same thing.
// A panel that printed one line for both would tell the operator to retry the
// one of them retrying cannot fix.
const TOO_LONG = new ApiError(400, null, 'The reason is longer than 2000 characters.')
const BAD_REQUEST_TEXT = 'The reason is longer than 2000 characters.'
const BUSY_TEXT = 'The database is busy.'

// MeetingsAdminEndpoints.MailUnconfiguredCode's own sentence, shortened. A
// proposal is a question, and the only channel that can carry it is the mail.
const CONFLICT = 409
const MAIL_UNCONFIGURED_TEXT = 'A new time cannot be suggested until Email is configured.'

describe('MeetingActions', () => {
  beforeEach(() => {
    // Only Date is faked, so `waitFor` still runs on real timers. The picker
    // opens on today's month in the ORGANIZER's zone, and the slots below have
    // to land inside it — the same arrangement manage.test.tsx makes.
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date(TODAY))
    vi.mocked(getSlots).mockResolvedValue([PROPOSED_SLOT, OTHER_SLOT])
  })

  // Cleared after the test rather than reset before it, which is what
  // meetings.test.tsx does and is not a stylistic choice: mockReset drops the
  // recorded results, and vitest reads a rejected result it no longer has a
  // record of being awaited as an unhandled rejection — so every test here that
  // exercises a failure would fail on the failure it is asserting.
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
    vi.useRealTimers()
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
    vi.mocked(cancelMeeting).mockRejectedValue(new ApiError(503, 'database_busy', BUSY_TEXT))
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

    // 2000 is CancelMeetingRequest.MaxReasonLength. Over it the server answers
    // 400, which is a failure the operator can do nothing about after the fact.
    expect(screen.getByLabelText(/why/i)).toHaveProperty('maxLength', 2000)
  })

  test('does not cancel until the panel is confirmed', () => {
    renderActions()

    fireEvent.click(screen.getByRole('button', { name: /cancel/i }))

    expect(cancelMeeting).not.toHaveBeenCalled()
  })

  test('the propose panel reads the link own slots, keyed on the slug and the meeting length', async () => {
    renderActions()

    fireEvent.click(screen.getByRole('button', { name: /suggest a new time/i }))

    // The slug, because GET /links/{slug}/slots takes no link id — and the
    // booking's own length, because a suggestion of a different length would be
    // a different meeting. The month is the whole window, never a day at a time.
    await waitFor(() =>
      expect(getSlots).toHaveBeenCalledWith(
        SLUG,
        expect.objectContaining({ duration: DURATION_MINUTES }),
        expect.anything(),
      ),
    )
  })

  test('proposes the slot the operator picked, verbatim', async () => {
    vi.mocked(proposeMeeting).mockResolvedValue(undefined as never)
    const changed = vi.fn()
    render(
      <MeetingActions
        meetingId={MEETING_ID}
        slug={SLUG}
        durationMinutes={DURATION_MINUTES}
        zone={ORGANIZER_ZONE}
        proposal={null}
        onChanged={changed}
      />,
    )

    fireEvent.click(screen.getByRole('button', { name: /suggest a new time/i }))
    fireEvent.click(await screen.findByRole('button', { name: new RegExp(PROPOSED_TIME) }))
    fireEvent.click(screen.getByRole('button', { name: /suggest this time/i }))

    // The chosen string goes back exactly as it arrived. Rebuilt as an offset
    // form it is refused as instant_invalid.
    await waitFor(() => expect(proposeMeeting).toHaveBeenCalledWith(MEETING_ID, PROPOSED_SLOT))
    await waitFor(() => expect(changed).toHaveBeenCalled())
  })

  test('nothing is suggested until a time is picked', async () => {
    renderActions()

    fireEvent.click(screen.getByRole('button', { name: /suggest a new time/i }))
    await screen.findByRole('button', { name: new RegExp(PROPOSED_TIME) })

    // The commit is absent rather than disabled: a button that cannot be
    // pressed still reads as an action, and there is no action here yet.
    expect(screen.queryByRole('button', { name: /suggest this time/i })).toBeNull()
    expect(proposeMeeting).not.toHaveBeenCalled()
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

    await waitFor(() =>
      expect(withdrawProposal).toHaveBeenCalledWith(MEETING_ID, PROPOSAL_ID),
    )
  })

  test('a lapsed proposal is offered again rather than offered for withdrawal', () => {
    // The server folds the clock into the state, so `expired` arrives whether
    // or not anything has swept it. Withdraw over one would ask the operator to
    // close something already closed, and would leave them no way to re-offer.
    renderActions({ ...openProposal, state: 'expired' })

    expect(screen.getByRole('button', { name: /suggest a new time/i })).toBeDefined()
    expect(screen.queryByRole('button', { name: /withdraw/i })).toBeNull()
  })

  test('a refused proposal keeps the panel open with the server own sentence', async () => {
    vi.mocked(proposeMeeting).mockRejectedValue(
      new ApiError(CONFLICT, 'mail_unconfigured', MAIL_UNCONFIGURED_TEXT),
    )
    renderActions()

    fireEvent.click(screen.getByRole('button', { name: /suggest a new time/i }))
    fireEvent.click(await screen.findByRole('button', { name: new RegExp(PROPOSED_TIME) }))
    fireEvent.click(screen.getByRole('button', { name: /suggest this time/i }))

    expect((await screen.findByRole('alert')).textContent).toContain(MAIL_UNCONFIGURED_TEXT)
    // Still armed, and the choice still standing: nothing was held, so sending
    // the operator back to pick the same time again would be busywork.
    expect(screen.getByRole('button', { name: /suggest this time/i })).toBeDefined()
  })

  test('every class the propose panel renders exists in kairos.css', async () => {
    const css = readFileSync(stylesheet, 'utf8')
    const { container } = render(
      <MeetingActions
        meetingId={MEETING_ID}
        slug={SLUG}
        durationMinutes={DURATION_MINUTES}
        zone={ORGANIZER_ZONE}
        proposal={null}
        onChanged={vi.fn()}
      />,
    )

    fireEvent.click(screen.getByRole('button', { name: /suggest a new time/i }))
    await screen.findByRole('button', { name: new RegExp(PROPOSED_TIME) })

    const used = new Set(
      [...container.querySelectorAll('[class]')].flatMap((element) => [...element.classList]),
    )

    // This task adds no CSS, so a class this markup invents is a class that
    // styles nothing — and the failure is silent: the panel simply stops
    // keeping its shape, in a viewport no unit test can see.
    expect([...used].filter((name) => !css.includes(`.${name}`))).toEqual([])
  })
})
