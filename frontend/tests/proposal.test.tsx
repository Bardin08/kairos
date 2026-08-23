import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { Route, Router } from 'wouter'
import { memoryLocation } from 'wouter/memory-location'

import Proposal from '../src/pages/Proposal'
import {
  acceptProposal,
  ApiError,
  declineProposal,
  getProposal,
  type Proposal as Offer,
} from '../src/lib/api'

vi.mock('../src/lib/api', async (importOriginal) => ({
  // ApiError stays real: the page reads a failure's status with `asApiError`,
  // which branches on `instanceof`. A plain object would send a 404 down the
  // generic path and the "this link leads nowhere" test would pass without the
  // line it is about ever running.
  ...(await importOriginal<typeof import('../src/lib/api')>()),
  getProposal: vi.fn(),
  acceptProposal: vi.fn(),
  declineProposal: vi.fn(),
}))

// Resolved from the working directory rather than import.meta.url, the same way
// stylesheet.test.ts does it: under jsdom, Vite rewrites that to an http:// URL.
const stylesheet = resolve(process.cwd(), '../backend/src/Kairos.Api/wwwroot/kairos.css')

const TOKEN = 'a'.repeat(64)

// The ORGANIZER's, and deliberately neither UTC nor the zone the machine
// running these tests is in — every time assertion below is the proof that the
// page renders the zone the API sent rather than the browser's own.
const ORGANIZER_ZONE = 'Asia/Tokyo'

// September, so Tokyo is +09:00 throughout. 11:00Z is 20:00 there and 14:00Z is
// 23:00, which are two numbers a page reading the machine's clock cannot
// produce.
const CURRENT_START = '2030-09-09T11:00:00Z'
const CURRENT_END = '2030-09-09T11:30:00Z'
const CURRENT_TIME = '20:00'
const PROPOSED_START = '2030-09-09T14:00:00Z'
const PROPOSED_END = '2030-09-09T14:30:00Z'
const PROPOSED_TIME = '23:00'
const EXPIRES_AT = '2030-09-11T14:00:00Z'
const LINK_NAME = 'Consulting call'

const NOT_FOUND = 404
const CONFLICT = 409
const SERVER_ERROR = 500

const open: Offer = {
  bookingId: '0192f000-0000-7000-8000-000000000001',
  linkName: LINK_NAME,
  currentStart: CURRENT_START,
  currentEnd: CURRENT_END,
  proposedStart: PROPOSED_START,
  proposedEnd: PROPOSED_END,
  expiresAt: EXPIRES_AT,
  timeZone: ORGANIZER_ZONE,
  state: 'open',
}

function renderProposal() {
  const { hook } = memoryLocation({ path: `/proposal/${TOKEN}` })
  // Mounted through the route it actually ships behind rather than bare: the
  // page reads its token out of the path, so a bare render would hand it
  // nothing and every test here would assert against the not-found page.
  render(
    <Router hook={hook}>
      <Route path="/proposal/:token" component={Proposal} />
    </Router>,
  )
}

beforeEach(() => {
  vi.mocked(getProposal).mockResolvedValue(open)
})

// Cleared after the test rather than reset before it, which is what
// meetingActions.test.tsx does and is not a stylistic choice: mockReset drops
// the recorded results, and vitest reads a rejected result it no longer has a
// record of being awaited as an unhandled rejection — so every test here that
// exercises a failure would fail on the failure it is asserting.
afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

test('shows what is being left and what is being offered, both in the organizer zone', async () => {
  renderProposal()

  expect(await screen.findByText(LINK_NAME)).toBeDefined()
  expect(screen.getByText(new RegExp(CURRENT_TIME))).toBeDefined()
  expect(screen.getByText(new RegExp(PROPOSED_TIME))).toBeDefined()
  expect(screen.getByText(new RegExp(ORGANIZER_ZONE))).toBeDefined()
})

test('accepting sends the token from the path and nothing else', async () => {
  vi.mocked(acceptProposal).mockResolvedValue(undefined as never)
  renderProposal()

  fireEvent.click(await screen.findByRole('button', { name: /accept/i }))

  await waitFor(() => expect(acceptProposal).toHaveBeenCalledWith(TOKEN))
  expect(declineProposal).not.toHaveBeenCalled()
})

test('an accepted proposal says the meeting moved rather than that it was answered', async () => {
  vi.mocked(acceptProposal).mockResolvedValue(undefined as never)
  renderProposal()

  fireEvent.click(await screen.findByRole('button', { name: /accept/i }))

  // The new time, and a sentence that is true of accepting in particular.
  // "Already answered" is the wrong thing to tell somebody who just agreed.
  expect(await screen.findByText(/has been moved/i)).toBeDefined()
  expect(screen.getByText(new RegExp(PROPOSED_TIME))).toBeDefined()
  expect(screen.queryByRole('button', { name: /accept/i })).toBeNull()
})

test('declining sends the token and leaves the meeting where it is', async () => {
  vi.mocked(declineProposal).mockResolvedValue({ ...open, state: 'declined' })
  renderProposal()

  fireEvent.click(await screen.findByRole('button', { name: /decline/i }))

  await waitFor(() => expect(declineProposal).toHaveBeenCalledWith(TOKEN))
  expect(await screen.findByText(/stays where it is/i)).toBeDefined()
  // The time it keeps is the one it had, not the one that was offered.
  expect(screen.getByText(new RegExp(CURRENT_TIME))).toBeDefined()
  expect(acceptProposal).not.toHaveBeenCalled()
})

test('a proposal answered before this page opened is finished, not broken', async () => {
  vi.mocked(getProposal).mockResolvedValue({ ...open, state: 'declined' })
  renderProposal()

  expect(await screen.findByText(/declined/i)).toBeDefined()
  expect(screen.queryByRole('button', { name: /accept/i })).toBeNull()
  expect(screen.queryByRole('button', { name: /decline/i })).toBeNull()
})

test('a lapsed proposal says nobody answered rather than that somebody did', async () => {
  vi.mocked(getProposal).mockResolvedValue({ ...open, state: 'expired' })
  renderProposal()

  // Two different things happened, and one sentence for both would tell an
  // invitee who let it lapse that they declined it.
  expect(await screen.findByText(/lapsed/i)).toBeDefined()
  expect(screen.queryByText(/declined/i)).toBeNull()
})

test('the state comes from the server even when the lapse time has passed', async () => {
  vi.useFakeTimers({ toFake: ['Date'] })
  // A day after the hold lapsed, with the server still calling it open. It
  // cannot happen against the real API — GET folds the clock in — and that is
  // the point: this fails the moment the page starts deriving expiry itself,
  // which would be a second opinion about a clock that is not this browser's.
  vi.setSystemTime(new Date('2030-09-12T14:00:00Z'))
  try {
    renderProposal()

    expect(await screen.findByRole('button', { name: /accept/i })).toBeDefined()
  } finally {
    vi.useRealTimers()
  }
})

test('a token that matches nothing leads nowhere rather than looking broken', async () => {
  vi.mocked(getProposal).mockRejectedValue(new ApiError(NOT_FOUND, null, 'Proposal not found.'))
  renderProposal()

  expect(await screen.findByText(/does not lead anywhere/i)).toBeDefined()
})

test('a server that is not answering does not tell the invitee their meeting is gone', async () => {
  vi.mocked(getProposal).mockRejectedValue(new ApiError(SERVER_ERROR, null, `The server answered ${SERVER_ERROR}.`))
  renderProposal()

  // A 500 rendered as "this link does not lead anywhere" would say the suggestion is
  // gone while it sits there intact.
  expect(await screen.findByText(/went wrong/i)).toBeDefined()
  expect(screen.queryByText(/does not lead anywhere/i)).toBeNull()
})

test('an answer that lost a race sends the invitee to the page rather than guessing which way', async () => {
  vi.mocked(acceptProposal).mockRejectedValue(
    new ApiError(CONFLICT, 'proposal_closed', 'This suggestion has already been answered.'),
  )
  renderProposal()

  fireEvent.click(await screen.findByRole('button', { name: /accept/i }))

  // A 409 here is either "somebody answered" or "it lapsed", and both are
  // answered by what a reload shows rather than by a sentence guessing.
  expect((await screen.findByRole('alert')).textContent).toMatch(/reload/i)
  // Still armed: nothing was recorded, so the buttons have to stay pressable.
  expect(screen.getByRole('button', { name: /accept/i })).toBeDefined()
})

test('every class the page renders exists in kairos.css', async () => {
  const css = readFileSync(stylesheet, 'utf8')
  const { container } = render(
    <Router hook={memoryLocation({ path: `/proposal/${TOKEN}` }).hook}>
      <Route path="/proposal/:token" component={Proposal} />
    </Router>,
  )
  await screen.findByText(LINK_NAME)

  const used = new Set(
    [...container.querySelectorAll('[class]')].flatMap((element) => [...element.classList]),
  )

  // This task adds no CSS, so a class this markup invents is a class that
  // styles nothing — and the failure is silent: the card simply stops keeping
  // its shape, in a viewport no unit test can see.
  expect([...used].filter((name) => !css.includes(`.${name}`))).toEqual([])
})
