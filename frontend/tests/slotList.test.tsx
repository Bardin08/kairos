import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { Route, Router } from 'wouter'
import { memoryLocation } from 'wouter/memory-location'

import { getSlots } from '../src/lib/api'
import Book from '../src/pages/Book'

// The invitee's zone, which every rendered time is in. Fixed here so an
// assertion means the same thing on a laptop in Warsaw and in CI.
process.env.TZ = 'UTC'

const SLUG = 'consulting'
const ORIGIN_LESS_MANAGE_PATH = '/manage/bdef4bc6'

const LINK = {
  slug: SLUG,
  name: 'Consulting call',
  description: 'A chat about your system.',
  // Deliberately not ascending: the link's own order is the render order.
  durations: [25, 50],
  timeZone: 'UTC',
}

// 2026-09-07 is a Monday. Two days carry times, 28 do not.
const SLOTS_25 = [
  '2026-09-07T09:00:00Z',
  '2026-09-07T09:30:00Z',
  '2026-09-08T09:00:00Z',
]

// A different DAY, not a subset of the list above. No client-side filter of
// SLOTS_25 can produce this, which is the point of the fixture.
const SLOTS_50 = ['2026-09-09T14:00:00Z']

const CREATED = {
  id: '01a0016c-0000-0000-0000-000000000000',
  slug: SLUG,
  start: SLOTS_25[0],
  end: '2026-09-07T09:25:00Z',
  durationMinutes: 25,
  status: 'confirmed',
  sequence: 0,
  name: 'Vlad',
  email: 'v@example.com',
  timeZone: 'UTC',
  notes: '',
  manageToken: 'bdef4bc6',
}

type Handler = (
  url: string,
  init?: RequestInit,
) => { status: number; body: unknown; retryAfter?: string }

function stubFetch(handler: Handler) {
  const spy = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const { status, body, retryAfter } = handler(String(input), init)
    return Promise.resolve({
      ok: status < 400,
      status,
      headers: new Headers(retryAfter === undefined ? {} : { 'Retry-After': retryAfter }),
      json: () => Promise.resolve(body),
    } as unknown as Response)
  })
  vi.stubGlobal('fetch', spy)
  return spy
}

/** The default server: a healthy link whose slots depend on the duration. */
function healthy(): Handler {
  return (url) => {
    if (url.includes('/slots')) {
      return { status: 200, body: url.includes('duration=50') ? SLOTS_50 : SLOTS_25 }
    }
    if (url.endsWith('/bookings')) return { status: 201, body: CREATED }
    return { status: 200, body: LINK }
  }
}

function renderBook() {
  const { hook } = memoryLocation({ path: `/book/${SLUG}` })
  render(
    <Router hook={hook}>
      <Route path="/book/:slug" component={Book} />
    </Router>,
  )
}

function times(): HTMLElement[] {
  return within(screen.getByRole('group', { name: 'Available times' })).getAllByRole('button')
}

function slotRequests(spy: ReturnType<typeof stubFetch>): string[] {
  return spy.mock.calls.map((call) => String(call[0])).filter((url) => url.includes('/slots'))
}

async function fillAndSubmit() {
  fireEvent.click(times()[0])
  fireEvent.click(screen.getAllByRole('button', { name: 'Continue' })[0])
  fireEvent.change(await screen.findByLabelText('Your name'), {
    target: { value: 'Vlad' },
  })
  fireEvent.change(screen.getByLabelText('Email'), {
    target: { value: 'v@example.com' },
  })
  fireEvent.click(screen.getByRole('button', { name: 'Confirm booking' }))
}

beforeEach(() => {
  // Date alone, so waitFor's own timers keep running. The month on screen is
  // otherwise whatever month the test happens to be run in.
  vi.useFakeTimers({ toFake: ['Date'] })
  vi.setSystemTime(new Date('2026-09-01T12:00:00Z'))
})

afterEach(() => {
  cleanup()
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

// Breaks if the slot column stops being a group of pressable times, or if
// selection stops being carried by aria-pressed — which is the only thing
// telling a screen reader which time is chosen, since the paint is colour.
test('a slot is pressed when chosen, and it is the only one', async () => {
  stubFetch(healthy())
  renderBook()

  await waitFor(() => expect(times()).toHaveLength(2))
  expect(times().map((slot) => slot.getAttribute('aria-pressed'))).toEqual(['false', 'false'])

  fireEvent.click(times()[1])

  expect(times().map((slot) => slot.getAttribute('aria-pressed'))).toEqual(['false', 'true'])
})

// Breaks if anyone reimplements the duration picker as a client-side filter of
// the times already fetched. A longer meeting closes whole DAYS, so the second
// fixture lands on a day the first never had.
test('changing the duration refetches the range rather than filtering it', async () => {
  const spy = stubFetch(healthy())
  renderBook()

  await waitFor(() => expect(slotRequests(spy)).toHaveLength(1))
  expect(slotRequests(spy)[0]).toContain('duration=25')
  screen.getByRole('heading', { name: 'Monday 7 September' })

  fireEvent.click(screen.getByRole('button', { name: '50 min' }))

  await waitFor(() => expect(slotRequests(spy)).toHaveLength(2))
  expect(slotRequests(spy)[1]).toContain('duration=50')
  // One request per month, and a month is the widest range the endpoint takes.
  expect(slotRequests(spy)[1]).toContain('from=2026-09-01&to=2026-09-30')

  await screen.findByRole('heading', { name: 'Wednesday 9 September' })
  expect(times()).toHaveLength(1)
})

// Breaks if closed days are dropped from the grid instead of dimmed. An
// invitee has to see that a month is mostly full, not be handed a calendar
// with holes punched in it.
test('days with no times stay in the grid, marked unavailable', async () => {
  stubFetch(healthy())
  renderBook()

  await waitFor(() => expect(times()).toHaveLength(2))

  const days = within(screen.getByRole('group', { name: 'September 2026' })).getAllByRole(
    'button',
    { hidden: true },
  )

  expect(days).toHaveLength(30)
  expect(days.filter((day) => day.getAttribute('data-state') === 'available')).toHaveLength(2)
  expect(days.filter((day) => day.getAttribute('data-state') === 'unavailable')).toHaveLength(28)
})

// Breaks if a lost race leaves the dead slot on screen, or if the explanation
// migrates to a banner at the top of the page instead of sitting by the times.
test('a 409 drops the slot and explains it beside the times', async () => {
  stubFetch((url) => {
    if (url.includes('/slots')) return { status: 200, body: SLOTS_25 }
    if (url.endsWith('/bookings')) {
      return {
        status: 409,
        body: { title: 'That time was taken.', status: 409, code: 'slot_taken' },
      }
    }
    return { status: 200, body: LINK }
  })
  renderBook()

  await waitFor(() => expect(times()).toHaveLength(2))
  await fillAndSubmit()

  const alert = await screen.findByRole('alert')
  expect(alert.textContent).toContain('That time was taken.')
  expect(times()).toHaveLength(1)
  expect(times()[0].textContent).toBe('09:30')
})

// Breaks if the one response that ever carries the manage token stops carrying
// it into a URL. The server keeps only a hash; a token lost here is a booking
// nobody can cancel, forever.
test('the manage token from the 201 lands in a URL the invitee can keep', async () => {
  const spy = stubFetch(healthy())
  renderBook()

  await waitFor(() => expect(times()).toHaveLength(2))
  await fillAndSubmit()

  const field = (await screen.findByLabelText(
    'Your link to move or cancel this booking',
  )) as HTMLInputElement

  expect(field.value.endsWith(ORIGIN_LESS_MANAGE_PATH)).toBe(true)
  expect(screen.getByRole('link', { name: 'Open it' }).getAttribute('href')).toBe(field.value)

  // And the instant went back exactly as it arrived — an offset form is refused.
  const created = spy.mock.calls.find((call) => String(call[0]).endsWith('/bookings'))
  expect(JSON.parse(String(created?.[1]?.body))).toMatchObject({
    start: SLOTS_25[0],
    durationMinutes: 25,
    timeZone: 'UTC',
  })
})

// Breaks if one corrupt availability rule is rendered as a quiet empty day.
// It closes every date the rule's weekday appears in, so there is no other day
// to offer and nothing a month button can fix.
test('a corrupt availability pattern takes the whole page', async () => {
  stubFetch((url) => {
    if (url.includes('/slots')) {
      return {
        status: 400,
        body: { title: 'Stored availability is unreadable.', status: 400, code: 'pattern_invalid' },
      }
    }
    return { status: 200, body: LINK }
  })
  renderBook()

  await screen.findByRole('heading', { name: 'This calendar cannot be read' })
  expect(screen.queryByRole('group', { name: 'September 2026' })).toBeNull()
})

// Breaks if a contended write is treated like a lost race. Nothing was written,
// so the time is still available and everything typed is still valid — telling
// this user to pick another time would take a slot away from them for nothing.
test('a 503 keeps the slot, the typed details and the armed button', async () => {
  stubFetch((url) => {
    if (url.includes('/slots')) return { status: 200, body: SLOTS_25 }
    if (url.endsWith('/bookings')) {
      return {
        status: 503,
        body: { title: 'The database was busy. Try again.', status: 503, code: 'database_busy' },
        retryAfter: '5',
      }
    }
    return { status: 200, body: LINK }
  })
  renderBook()

  await waitFor(() => expect(times()).toHaveLength(2))
  await fillAndSubmit()

  const alert = await screen.findByRole('alert')
  expect(alert.textContent).toContain('nothing was booked')
  // The server's own wait, not one invented here.
  expect(alert.textContent).toContain('in 5 seconds')

  // Still on the form, with what was typed, and the button ready to send again.
  expect((screen.getByLabelText('Your name') as HTMLInputElement).value).toBe('Vlad')
  expect((screen.getByLabelText('Email') as HTMLInputElement).value).toBe('v@example.com')
  expect(screen.getByRole('button', { name: 'Confirm booking' })).not.toBeNull()
  expect(screen.queryByRole('group', { name: 'Available times' })).toBeNull()
})

// Breaks if a link taken down mid-session is rendered as a quiet empty month.
// Both link routes answer `not_found` for a slug that resolves to nothing, and
// the page must key on the code rather than either route's wording.
test('a link that stops existing mid-session takes the whole page', async () => {
  stubFetch((url) => {
    if (url.includes('/slots')) {
      return {
        status: 404,
        body: { title: "Link 'consulting' does not exist.", status: 404, code: 'not_found' },
      }
    }
    return { status: 200, body: LINK }
  })
  renderBook()

  await screen.findByRole('heading', { name: 'This link stopped taking bookings' })
  expect(screen.queryByRole('group', { name: 'September 2026' })).toBeNull()
})

// Breaks if the two 400 shapes are conflated. A DataAnnotations failure has no
// `code` at all, and a caller that keys on codes must read that as generic
// rather than as a code it has not learned yet.
test('a 400 carries its code only when the server sent one', async () => {
  stubFetch(() => ({
    status: 400,
    body: { title: 'Pick a duration.', status: 400, code: 'duration_required' },
  }))
  await expect(getSlots(SLUG, { from: '2026-09-01', to: '2026-09-30' })).rejects.toMatchObject({
    status: 400,
    code: 'duration_required',
    message: 'Pick a duration.',
  })

  stubFetch(() => ({
    status: 400,
    body: {
      title: 'One or more validation errors occurred.',
      status: 400,
      errors: { Slug: ['The field Slug is invalid.'] },
    },
  }))
  await expect(getSlots(SLUG, { from: '2026-09-01', to: '2026-09-30' })).rejects.toMatchObject({
    status: 400,
    code: null,
  })
})
