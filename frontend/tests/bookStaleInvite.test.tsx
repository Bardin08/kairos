import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
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
const NOT_FOUND = 404

const DURATION_MINUTES = 25

const LINK = {
  slug: SLUG,
  name: 'Consulting call',
  description: 'A chat about your system.',
  durations: [DURATION_MINUTES],
  // The ORGANIZER's zone. The same value as the invitee's here, so the times
  // this test reads off the screen are unambiguous.
  timeZone: INVITEE_ZONE,
}

// Two times on one day: 2026-09-07 is a Monday, inside the month the clock is
// frozen in. Two rather than one, so "the times are still offered" is a list
// rather than a single button that could be something else on the page.
const SLOTS = ['2026-09-07T09:00:00Z', '2026-09-07T09:30:00Z']

// What the operator filled in when the invite was issued. Carried on every
// fixture below, including the stale ones: the server answers a stale invite's
// prefill intact, so "nothing was filled in for you" is the PAGE's decision and
// not something the fixture arranges by leaving the bag empty.
const PREFILL = { name: 'Ada Lovelace', email: 'ada@example.com' }

// The deadline an expired invite lapsed on, and the sentence the page builds
// out of it. Written out rather than formatted here: formatting it would be this
// test carrying its own copy of lib/time and passing whatever that was changed
// to. en-GB and UTC are both fixed, so this string is not machine-dependent.
const DEADLINE = '2026-08-31T17:00:00Z'
const DEADLINE_ON_SCREEN = 'Monday 31 August, 17:00'

// The state names the API answers, lower-cased the way a row carries them.
const CONSUMED = 'consumed'
const REVOKED = 'revoked'
const EXPIRED = 'expired'

/** The problem body the API answers for a token no invite has. */
const NO_SUCH_INVITE = { code: 'not_found', title: 'No such invitation' }

type InviteResponse = { status: number; body: unknown }

function staleInvite(state: string, expiresAt: string | null = null): InviteResponse {
  return { status: OK, body: { slug: SLUG, state, expiresAt, prefill: PREFILL } }
}

/** A healthy link and its times, with the invite read the case is about. */
function stubFetch(invite: InviteResponse) {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL) => {
      const url = String(input)
      const { status, body } =
        url.includes('/slots') ? { status: OK, body: SLOTS }
        : url.includes('/invites/') ? invite
        : { status: OK, body: LINK }

      return Promise.resolve({
        ok: status < 400,
        status,
        headers: new Headers(),
        json: () => Promise.resolve(body),
      } as unknown as Response)
    }),
  )
}

function renderBook(invite: InviteResponse) {
  stubFetch(invite)
  render(
    <Router hook={memoryLocation({ path: `/book/${SLUG}` }).hook}>
      <Route path="/book/:slug" component={Book} />
    </Router>,
  )
}

function times(): HTMLElement[] {
  return within(screen.getByRole('group', { name: 'Available times' })).getAllByRole('button')
}

/** Everything the page announces about the invitation, which is empty when it has nothing to say. */
function announcement(): string {
  return document.querySelector('[role="status"]')?.textContent ?? ''
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
 * The clause the backend cannot state: it proves a stale token answers 200 with
 * its state rather than 404, and stops there. Whether the page then draws a
 * bookable form or a dead end is this side's decision, and the design's answer
 * is that a stale invitation is still a booking page somebody should be able to
 * use — the invitation closed, not the link.
 *
 * Both halves, because either alone passes a page that is wrong: a notice with
 * no times is the 404 this clause exists to forbid, and times with no notice
 * leave an invitee wondering why their name is missing.
 */
test('a used invitation says so, and every time below is still bookable', async () => {
  renderBook(staleInvite(CONSUMED))

  await waitFor(() => expect(times()).toHaveLength(SLOTS.length))
  expect(announcement()).toMatch(/has already been used/)
  // The recovery, not only the refusal. A closed invitation that does not say
  // what is still possible reads as a closed page.
  expect(announcement()).toMatch(/still open to book/)
  expect(screen.queryByRole('heading', { name: /does not lead anywhere/i })).toBeNull()

  fireEvent.click(times()[0])
  fireEvent.click(screen.getAllByRole('button', { name: 'Continue' })[0])

  // The other half of the sentence above: the prefill arrived and was not used,
  // so the invitee fills the form in themselves rather than sending a stranger's
  // name. Breaks if the seeding stops asking whether the invite is still open.
  const name = await screen.findByLabelText('Your name')
  expect((name as HTMLInputElement).value).toBe('')
})

/**
 * One clause per state, because "this did not work" is not something a reader
 * can act on — and withdrawn is the state whose cause is somebody else's
 * decision rather than the reader's own.
 */
test('a withdrawn invitation names the state that closed it', async () => {
  renderBook(staleInvite(REVOKED))

  await waitFor(() => expect(times()).toHaveLength(SLOTS.length))
  expect(announcement()).toMatch(/was withdrawn/)
})

/**
 * The one state that names a date. "Expired" alone leaves the reader unable to
 * tell whether they missed it by an hour or by a month, which decides whether
 * asking for a new one is worth their time.
 */
test('an expired invitation names the date it lapsed on', async () => {
  renderBook(staleInvite(EXPIRED, DEADLINE))

  await waitFor(() => expect(times()).toHaveLength(SLOTS.length))
  expect(announcement()).toContain(`expired on ${DEADLINE_ON_SCREEN}`)
})

/**
 * The case most likely to regress into a 404 page, and the one the API answers
 * differently from the other three: a token nobody issued is a genuine 404 on
 * the invite read. It is still an ordinary booking link, and the page has
 * nothing to tell anyone — naming the failure would only confirm to a guesser
 * that the token was merely wrong.
 */
test('a token nobody issued draws the ordinary form with nothing to explain', async () => {
  renderBook({ status: NOT_FOUND, body: NO_SUCH_INVITE })

  await waitFor(() => expect(times()).toHaveLength(SLOTS.length))
  expect(announcement()).toBe('')
  expect(document.body.textContent).not.toMatch(/invitation/i)
})
