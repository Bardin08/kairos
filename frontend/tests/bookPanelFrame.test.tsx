import { cleanup, render, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { Route, Router } from 'wouter'
import { memoryLocation } from 'wouter/memory-location'

import Book from '../src/pages/Book'

// The structural half of the panel-frame fix, which is the half jsdom can see.
// The visual half — that the frame is as tall as a full slot column — is pinned
// in stylesheet.test.ts, because jsdom performs no layout and every offsetHeight
// here would be 0.
//
// What this file protects: a later edit moving one of the four branches out of
// the framed element. That is invisible in review and silently reintroduces the
// jump this fix removed, because three of the four branches do not render
// SlotList at all and so never carried the old floor.
const FRAME = '.slot-panel'

const SLUG = 'consulting'
const OK = 200
const SERVER_ERROR = 500
const DURATION_MINUTES = 25

const LINK = {
  slug: SLUG,
  name: 'Consulting call',
  durations: [DURATION_MINUTES],
  timeZone: 'UTC',
}

/** Two times on one day of the month the clock below is pinned to. */
const SLOTS = ['2026-09-07T09:00:00Z', '2026-09-07T09:30:00Z']

/** A month with no gap long enough — the page's own empty state, not an error. */
const NO_SLOTS: string[] = []

type SlotsAnswer = { status: number; body: unknown } | 'pending'

function stubFetch(slots: SlotsAnswer) {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL) => {
      const url = String(input)

      if (url.includes('/slots')) {
        // A promise that never settles is the loading state, held open for as
        // long as the assertion needs rather than raced against a timer.
        if (slots === 'pending') return new Promise<Response>(() => {})

        return Promise.resolve({
          ok: slots.status < 400,
          status: slots.status,
          headers: new Headers(),
          json: () => Promise.resolve(slots.body),
        } as unknown as Response)
      }

      return Promise.resolve({
        ok: true,
        status: OK,
        headers: new Headers(),
        json: () => Promise.resolve(LINK),
      } as unknown as Response)
    }),
  )
}

function renderBook(slots: SlotsAnswer) {
  stubFetch(slots)
  render(
    <Router hook={memoryLocation({ path: `/book/${SLUG}` }).hook}>
      <Route path="/book/:slug" component={Book} />
    </Router>,
  )
}

function frame(): HTMLElement {
  const panel = document.querySelector(FRAME)
  expect(panel).not.toBeNull()
  return panel as HTMLElement
}

beforeEach(() => {
  window.history.replaceState(null, '', `/book/${SLUG}`)
  // Date alone, so waitFor's timers keep running. The month on screen is
  // otherwise whatever month the suite happens to be run in.
  vi.useFakeTimers({ toFake: ['Date'] })
  vi.setSystemTime(new Date('2026-09-01T12:00:00Z'))
})

afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
  cleanup()
})

test('the loading state renders inside the frame', async () => {
  renderBook('pending')

  await waitFor(() => expect(frame().textContent).toContain('Finding open times'))
})

test('the loaded state renders inside the frame', async () => {
  renderBook({ status: OK, body: SLOTS })

  await waitFor(() =>
    expect(within(frame()).getByRole('group', { name: 'Available times' })).toBeTruthy(),
  )
})

test('the empty month renders inside the frame', async () => {
  renderBook({ status: OK, body: NO_SLOTS })

  await waitFor(() => expect(frame().textContent).toMatch(/gap long enough/))
})

test('the error state renders inside the frame', async () => {
  renderBook({ status: SERVER_ERROR, body: { code: 'server_error', title: 'Something broke' } })

  await waitFor(() =>
    expect(within(frame()).getByRole('button', { name: 'Try again' })).toBeTruthy(),
  )
})
