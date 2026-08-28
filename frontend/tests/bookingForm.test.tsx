import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'

import BookingForm from '../src/components/BookingForm'
import { ApiError, createBooking, INVITE_SPENT } from '../src/lib/api'
import type { Link } from '../src/lib/api'

vi.mock('../src/lib/api', async (importOriginal) => ({
  // ApiError and the codes stay real: the branch under test reads `code`, and a
  // plain object would send every failure down the generic path while these
  // tests still passed.
  ...(await importOriginal<typeof import('../src/lib/api')>()),
  createBooking: vi.fn(),
}))

const CONFLICT = 409

// The other 409 this form can take, which has no constant of its own in
// lib/api.ts because nothing keys on it — the status alone decides that branch.
const SLOT_TAKEN = 'slot_taken'

const link: Link = {
  slug: 'consulting',
  name: 'Consulting call',
  description: '',
  durations: [25],
  timeZone: 'Europe/Warsaw',
}

const START = '2026-09-07T09:00:00Z'
const DURATION_MINUTES = 25
const BOOKER_ZONE = 'Europe/Warsaw'

// Valid on the form's own rules, so every submit below reaches the network
// rather than stopping at the name and email checks.
const DETAILS = { name: 'Ada Lovelace', email: 'ada@example.com', notes: '' }

const INVITE_TOKEN = 'b'.repeat(64)

// The server's own sentence, verbatim. This one is asserted as pass-through — the
// form hands it to onSlotLost untouched — so a rewording there must not quietly
// become something this file asserts about instead.
const SLOT_TAKEN_MESSAGE = 'That time was taken while this booking was being made.'

// The server's own sentence too, but only as the INPUT. The form discards
// `problem.message` on this branch and writes its own copy, because the recovery
// is the half that matters and a bare refusal does not carry it — so the
// assertion below pins the frontend's wording rather than this string.
const INVITE_SPENT_MESSAGE = 'That personal invitation cannot be used any more.'

function renderForm(onSlotLost: () => void, inviteToken: string | null = INVITE_TOKEN) {
  render(
    <BookingForm
      link={link}
      start={START}
      durationMinutes={DURATION_MINUTES}
      zone={BOOKER_ZONE}
      details={DETAILS}
      inviteToken={inviteToken}
      onDetails={vi.fn()}
      onBack={vi.fn()}
      onBooked={vi.fn()}
      onSlotLost={onSlotLost}
    />,
  )
}

function confirm() {
  fireEvent.click(screen.getByRole('button', { name: /confirm booking/i }))
}

afterEach(() => {
  cleanup()
  vi.resetAllMocks()
})

/**
 * The defect this branch exists to prevent. `invite_spent` is a 409 like a taken
 * slot, but nothing was booked and the time is still free — routing it through
 * onSlotLost strikes a time off the list that somebody can still have, and sends
 * the invitee back to the calendar to pick a different one for no reason.
 */
test('a spent invitation keeps the slot and reports the invitation', async () => {
  const onSlotLost = vi.fn()
  vi.mocked(createBooking).mockRejectedValue(
    new ApiError(CONFLICT, INVITE_SPENT, INVITE_SPENT_MESSAGE),
  )

  renderForm(onSlotLost)
  confirm()

  expect(await screen.findByText(/personal invitation cannot be used/i)).toBeTruthy()
  expect(onSlotLost).not.toHaveBeenCalled()
  // The recovery the message names, spelled out: the time is still theirs.
  expect(document.body.textContent).toMatch(/still free/i)
})

/** The other 409, unchanged: that time really is gone and the list has to lose it. */
test('a taken slot still goes back to the caller to be removed', async () => {
  const onSlotLost = vi.fn()
  vi.mocked(createBooking).mockRejectedValue(
    new ApiError(CONFLICT, SLOT_TAKEN, SLOT_TAKEN_MESSAGE),
  )

  renderForm(onSlotLost)
  confirm()

  await waitFor(() => expect(onSlotLost).toHaveBeenCalledWith(SLOT_TAKEN_MESSAGE))
})

/**
 * The failure above tells the invitee to send it again, so the second press has
 * to send something the server can accept. Breaks if the token is carried
 * unchanged, which makes that instruction a loop rather than a recovery.
 */
test('the token rides along once, and is dropped after the server refuses it', async () => {
  vi.mocked(createBooking).mockRejectedValueOnce(
    new ApiError(CONFLICT, INVITE_SPENT, INVITE_SPENT_MESSAGE),
  )

  renderForm(vi.fn())
  confirm()

  await screen.findByText(/personal invitation cannot be used/i)
  expect(vi.mocked(createBooking).mock.calls[0][0].inviteToken).toBe(INVITE_TOKEN)

  vi.mocked(createBooking).mockResolvedValueOnce({
    id: '01a0016c-0000-7000-8000-000000000001',
    slug: link.slug,
    start: START,
    end: '2026-09-07T09:25:00Z',
    durationMinutes: DURATION_MINUTES,
    status: 'confirmed',
    sequence: 0,
    name: DETAILS.name,
    email: DETAILS.email,
    timeZone: BOOKER_ZONE,
    notes: '',
    manageToken: 'c'.repeat(64),
    calendarSync: null,
    conferenceUrl: null,
  })
  confirm()

  await waitFor(() => expect(createBooking).toHaveBeenCalledTimes(2))
  expect(vi.mocked(createBooking).mock.calls[1][0].inviteToken).toBeUndefined()
})
