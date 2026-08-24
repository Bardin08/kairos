import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { Route, Router } from 'wouter'
import { memoryLocation } from 'wouter/memory-location'

import Manage from '../src/pages/Manage'
import { ApiError, cancelBooking, getBooking, getLink, getSlots, rescheduleBooking } from '../src/lib/api'
import type { Booking } from '../src/lib/api'

vi.mock('../src/lib/api', async (importOriginal) => ({
  // ApiError stays real, because Manage.tsx reads a failure with `instanceof`.
  // A plain object would send every error down the "carries no code" path, and
  // the tests below would pass while the code that reads `code` never ran.
  ...(await importOriginal<typeof import('../src/lib/api')>()),
  getBooking: vi.fn(),
  getLink: vi.fn(),
  getSlots: vi.fn(),
  cancelBooking: vi.fn(),
  rescheduleBooking: vi.fn(),
}))

const token = 'a'.repeat(64)

const confirmed: Booking = {
  id: '01a0016c-0000-7000-8000-000000000001',
  slug: 'consulting',
  start: '2026-09-07T09:00:00Z',
  end: '2026-09-07T09:25:00Z',
  durationMinutes: 25,
  status: 'confirmed',
  sequence: 0,
  name: 'Vlad',
  email: 'v@example.com',
  timeZone: 'Europe/Warsaw',
  notes: '',
  manageToken: null,
  // The state this deployment is in until somebody connects a calendar: nothing
  // was attempted, so there is nothing to report. Not the same as `failed`.
  calendarSync: null,
  conferenceUrl: null,
}

function renderManage() {
  const { hook } = memoryLocation({ path: `/manage/${token}` })
  // Mounted through the route it actually ships behind, not bare: the page
  // reads its token out of the path, so a bare render would hand it nothing and
  // every one of these tests would assert against the not-found page.
  render(
    <Router hook={hook}>
      <Route path="/manage/:token" component={Manage} />
    </Router>,
  )
}

beforeEach(() => {
  // Only Date is faked, so `waitFor` still runs on real timers. The month grid
  // opens on today's month, and the slots below have to land inside it.
  vi.useFakeTimers({ toFake: ['Date'] })
  vi.setSystemTime(new Date('2026-09-01T08:00:00Z'))
  vi.mocked(getLink).mockResolvedValue({
    slug: 'consulting',
    name: 'Consulting call',
    description: '',
    durations: [25],
    timeZone: 'UTC',
  })
})

// vitest runs without `globals`, so React Testing Library never installs its
// own afterEach and each render would otherwise stack in the same document.
afterEach(() => {
  cleanup()
  vi.useRealTimers()
  vi.resetAllMocks()
})

test('a token that matches nothing gets one honest page and no detail about why', async () => {
  vi.mocked(getBooking).mockRejectedValue(new ApiError(404, 'not_found', 'Booking does not exist.'))

  renderManage()

  expect(await screen.findByText(/does not lead anywhere/i)).toBeTruthy()
  // A 404 that named the reason would tell a guesser whether the token was
  // well-formed but wrong, or simply absent. Nothing may distinguish the two.
  expect(document.body.textContent).not.toMatch(/not_found|404/i)
})

test('a cancelled booking says so, keeps its facts, and offers no way to cancel again', async () => {
  vi.mocked(getBooking).mockResolvedValue({ ...confirmed, status: 'cancelled' })

  renderManage()

  expect(await screen.findByText('Cancelled')).toBeTruthy()
  // Honest rather than empty: the meeting it refers to is still on the page.
  expect(screen.getByText(/25 minutes/)).toBeTruthy()
  expect(screen.queryByRole('button', { name: /cancel/i })).toBeNull()
  expect(screen.queryByRole('button', { name: /reschedule/i })).toBeNull()
})

test('cancel fires only after it is confirmed', async () => {
  vi.mocked(getBooking).mockResolvedValue(confirmed)
  vi.mocked(cancelBooking).mockResolvedValue({ ...confirmed, status: 'cancelled' })

  renderManage()

  fireEvent.click(await screen.findByRole('button', { name: /cancel meeting/i }))
  // The destructive call must not have gone out on the first press. Wiring the
  // endpoint straight to that button is the defect this catches.
  expect(cancelBooking).not.toHaveBeenCalled()

  fireEvent.click(await screen.findByRole('button', { name: /yes, cancel it/i }))
  await waitFor(() => expect(cancelBooking).toHaveBeenCalledWith(token))
  expect(await screen.findByText('Cancelled')).toBeTruthy()
})

test('a cancel the server was too busy to take leaves the meeting confirmed and the action armed', async () => {
  vi.mocked(getBooking).mockResolvedValue(confirmed)
  vi.mocked(cancelBooking).mockRejectedValue(
    new ApiError(503, 'database_busy', 'The database was busy. Try again.', 5),
  )

  renderManage()

  fireEvent.click(await screen.findByRole('button', { name: /cancel meeting/i }))
  fireEvent.click(await screen.findByRole('button', { name: /yes, cancel it/i }))

  // The wait is the server's own Retry-After, quoted rather than invented.
  expect(await screen.findByText(/busy and nothing was changed\. Try again in 5 seconds/i)).toBeTruthy()
  // Nothing happened, so the page must not claim otherwise, and the only
  // action it has must still be reachable. A dead end or a false "cancelled"
  // are the two ways this goes wrong.
  expect(screen.getByText('Confirmed')).toBeTruthy()
  expect(screen.queryByText('Cancelled')).toBeNull()
  expect(screen.getByRole('button', { name: /yes, cancel it/i })).toBeTruthy()
})

test('a reschedule the server was too busy to take keeps the time the reader picked', async () => {
  vi.mocked(getBooking).mockResolvedValue(confirmed)
  vi.mocked(getSlots).mockResolvedValue(['2026-09-07T11:00:00Z', '2026-09-07T11:30:00Z'])
  vi.mocked(rescheduleBooking).mockRejectedValue(
    new ApiError(503, 'database_busy', 'The database was busy. Try again.', 5),
  )

  renderManage()

  fireEvent.click(await screen.findByRole('button', { name: /^reschedule$/i }))
  fireEvent.click(await screen.findByRole('button', { name: '13:00' }))
  fireEvent.click(screen.getAllByRole('button', { name: /^move/i })[0])

  expect(await screen.findByText(/server is busy and nothing was changed/i)).toBeTruthy()
  // 503 is the one refusal that does not mean "that instant is gone". Dropping
  // it would send the reader hunting for a slot that is still theirs.
  expect(screen.getByRole('button', { name: '13:00' }).getAttribute('aria-pressed')).toBe('true')
})

test('a refused reschedule reports beside the times, not as a banner above them', async () => {
  vi.mocked(getBooking).mockResolvedValue(confirmed)
  vi.mocked(getSlots).mockResolvedValue(['2026-09-07T11:00:00Z', '2026-09-07T11:30:00Z'])
  vi.mocked(rescheduleBooking).mockRejectedValue(
    new ApiError(409, 'slot_taken', 'That time was taken while this booking was being made.'),
  )

  renderManage()

  fireEvent.click(await screen.findByRole('button', { name: /^reschedule$/i }))
  // 11:00Z is 13:00 in the invitee's Warsaw, and the heading above it is the
  // organizer's day. Asking for the invitee's clock time is the assertion.
  const slot = await screen.findByRole('button', { name: '13:00' })
  fireEvent.click(slot)
  expect(slot.getAttribute('aria-pressed')).toBe('true')

  fireEvent.click(screen.getAllByRole('button', { name: /^move/i })[0])

  const error = await screen.findByText(/took that time while you were choosing/i)
  expect(error.className).toContain('field-error')
  // Position is the assertion, not just presence: the same sentence rendered
  // above the list is the banner DESIGN.md forbids.
  const slots = document.querySelector('.slots')
  expect(slots).not.toBeNull()
  expect(error.compareDocumentPosition(slots!) & Node.DOCUMENT_POSITION_PRECEDING).toBeTruthy()
  // And the time that was refused is gone, so it cannot be picked twice.
  expect(screen.queryByRole('button', { name: '13:00' })).toBeNull()
})

/**
 * The phase-1 condition that did not hold: the date rendered inside the button's
 * own label, in the proportional face, where the `desktop-cta` block of `Book.tsx`
 * puts the same date in a `numeric` span beside its button. Figures are the one
 * place typography is load-bearing on this product — and a button whose width
 * follows the date is the visible half of the same mistake.
 */
test('the reschedule CTA sets its date in the numeric face, beside the button', async () => {
  vi.mocked(getBooking).mockResolvedValue(confirmed)
  vi.mocked(getSlots).mockResolvedValue(['2026-09-07T11:00:00Z', '2026-09-07T11:30:00Z'])

  renderManage()

  fireEvent.click(await screen.findByRole('button', { name: /^reschedule$/i }))
  fireEvent.click(await screen.findByRole('button', { name: '13:00' }))

  // The verb alone. Every CTA on this page reads "Move" and none of them carries
  // a figure in its label.
  for (const cta of screen.getAllByRole('button', { name: /^move/i })) {
    expect(cta.textContent).toBe('Move')
  }

  // And the date is on the page, in the face figures are set in. The time the
  // click above chose, not merely a digit: /\d/ would pass on the date alone
  // and on a stale label from the previous selection.
  const when = document.querySelector('.desktop-cta .numeric')
  expect(when?.textContent).toContain('13:00')

  // Named by the verb, described by the date — otherwise the control announces
  // "Move" and never says where to. Both bars, because both render a button with
  // that label: the phone one is display:none beside this one at this width, never
  // absent, and an accessible name is not a thing a media query gets to differ on.
  for (const [bar, date] of [
    ['.desktop-cta', '.desktop-cta .numeric'],
    ['.actionbar', '.actionbar-when'],
  ]) {
    const cta = document.querySelector(`${bar} .btn-primary`)
    const describes = document.querySelector(date)
    expect(describes?.id).toBeTruthy()
    expect(cta?.getAttribute('aria-describedby')).toBe(describes?.id)
  }
})
