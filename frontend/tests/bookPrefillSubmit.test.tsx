import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { Route, Router } from 'wouter'
import { memoryLocation } from 'wouter/memory-location'

import Book from '../src/pages/Book'

// The invitee's zone, which every rendered time is in. Fixed so an assertion
// means the same thing on a laptop in Warsaw and in CI.
const INVITEE_ZONE = 'UTC'
process.env.TZ = INVITEE_ZONE

const SLUG = 'consulting'

// The query key the page reads a personal invite's token off. Written out here
// rather than imported: the constant is private to the page, and a test that
// imported it would pin the URL against itself rather than against the link the
// operator hands out.
const TOKEN_QUERY_KEY = 'i'

// A 64-character hex token, the width the API issues. Only the width matters.
const TOKEN = 'a'.repeat(64)

const OK = 200
const CREATED_STATUS = 201

// What the operator filled in on the invitee's behalf when the invite was issued.
const INVITED_NAME = 'Ada Lovelace'
const INVITED_EMAIL = 'ada@example.com'

// What the invitee puts over the operator's guess. Different from INVITED_NAME
// in a way no trim or case fold could produce, so the assertion cannot pass on
// the prefill having merely been reformatted.
const TYPED_NAME = 'Augusta King'

const DURATION_MINUTES = 25

const LINK = {
  slug: SLUG,
  name: 'Consulting call',
  description: 'A chat about your system.',
  durations: [DURATION_MINUTES],
  // The ORGANIZER's zone. The same value as the invitee's here, so the one time
  // this test reads off the screen is unambiguous.
  timeZone: INVITEE_ZONE,
}

// 2026-09-07 is a Monday, inside the month the clock below is frozen in.
const SLOTS = ['2026-09-07T09:00:00Z']

const CREATED = {
  id: '01a0016c-0000-0000-0000-000000000000',
  slug: SLUG,
  start: SLOTS[0],
  end: '2026-09-07T09:25:00Z',
  durationMinutes: DURATION_MINUTES,
  status: 'confirmed',
  sequence: 0,
  name: TYPED_NAME,
  email: INVITED_EMAIL,
  timeZone: INVITEE_ZONE,
  notes: '',
  manageToken: 'bdef4bc6',
  calendarSync: null,
  conferenceUrl: null,
}

const OPEN_INVITE = {
  slug: SLUG,
  state: 'open',
  expiresAt: null,
  prefill: { name: INVITED_NAME, email: INVITED_EMAIL },
}

const BOOKINGS_PATH = '/bookings'

const POST = 'POST'

/** The one call that creates the booking, told apart from the link read beside it by its verb. */
function isSubmit(url: string, init?: RequestInit): boolean {
  return url.endsWith(BOOKINGS_PATH) && init?.method === POST
}

/** A healthy server: the link, its times, the open invite, and a 201 on submit. */
function stubFetch() {
  const spy = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    const { status, body } =
      url.includes('/slots') ? { status: OK, body: SLOTS }
      : url.includes('/invites/') ? { status: OK, body: OPEN_INVITE }
      : isSubmit(url, init) ? { status: CREATED_STATUS, body: CREATED }
      : { status: OK, body: LINK }

    return Promise.resolve({
      ok: status < 400,
      status,
      headers: new Headers(),
      json: () => Promise.resolve(body),
    } as unknown as Response)
  })
  vi.stubGlobal('fetch', spy)
  return spy
}

/** The body of the POST that created the booking, parsed. */
function submitted(spy: ReturnType<typeof stubFetch>): Record<string, unknown> {
  const call = spy.mock.calls.find(([input, init]) => isSubmit(String(input), init))
  if (call === undefined) throw new Error('nothing was posted to /bookings')
  return JSON.parse(String(call[1]?.body)) as Record<string, unknown>
}

function renderBook() {
  render(
    <Router hook={memoryLocation({ path: `/book/${SLUG}` }).hook}>
      <Route path="/book/:slug" component={Book} />
    </Router>,
  )
}

function nameInput(): HTMLInputElement {
  return screen.getByLabelText('Your name') as HTMLInputElement
}

beforeEach(() => {
  // The token is read off the real address bar rather than off the router, so
  // the page has to be opened at the URL the operator handed out.
  window.history.replaceState(null, '', `/book/${SLUG}?${TOKEN_QUERY_KEY}=${TOKEN}`)
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

/**
 * The design's own promise: the prefill seeds a form and every field stays
 * editable. `applyPrefill` is a pure function that never submits, so on its own
 * it cannot say this — the load-bearing half is Book's `changeDetails`, which
 * both records the field as touched and moves the page's state. Break either
 * and the invitee watches themselves type a name the server never receives.
 *
 * Both halves are asserted at once: the edited field arrives edited, and the
 * field nobody touched still arrives carrying the operator's guess.
 */
test('an edited prefill submits the edit, and leaves the rest of the prefill standing', async () => {
  const spy = stubFetch()
  renderBook()

  fireEvent.click(await screen.findByRole('button', { name: /09:00/ }))
  fireEvent.click(screen.getAllByRole('button', { name: 'Continue' })[0])

  // The prefill really landed, so the edit below is an edit rather than typing
  // into an empty form — which would pass with no prefill at all.
  await waitFor(() => expect(nameInput().value).toBe(INVITED_NAME))

  fireEvent.change(nameInput(), { target: { value: TYPED_NAME } })
  expect(nameInput().value).toBe(TYPED_NAME)

  fireEvent.click(screen.getByRole('button', { name: 'Confirm booking' }))

  await waitFor(() => expect(submitted(spy).name).toBe(TYPED_NAME))

  const body = submitted(spy)
  expect(body.email).toBe(INVITED_EMAIL)
  expect(body.inviteToken).toBe(TOKEN)
})
