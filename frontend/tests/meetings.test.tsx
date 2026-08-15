import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { cleanup, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'

import Meetings from '../src/routes/admin/Meetings'
import { ApiError } from '../src/lib/api'
import { listMeetings } from '../src/routes/admin/adminMeetings'
import type { Meeting, Meetings as List } from '../src/routes/admin/adminMeetings'

// ApiError and asApiError are never mocked and are not in this module: the page
// reads a failure through `asApiError`, which branches on `instanceof`.
vi.mock('../src/routes/admin/adminMeetings', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/adminMeetings')>()),
  listMeetings: vi.fn(),
}))

// Same file the running app loads, and the authority on which class names exist.
const stylesheet = resolve(process.cwd(), '../backend/src/Kairos.Api/wwwroot/kairos.css')

// The organizer's, and deliberately neither UTC nor whatever zone the machine
// running these tests is in: the assertions below are the proof that the page
// renders the STORED setting rather than the browser's guess.
const ORGANIZER_ZONE = 'Europe/Warsaw'

// September, so Warsaw is +02:00 and Tokyo is +09:00. 09:00Z is 11:00 for the
// organizer and 18:00 for the booker — three different numbers for one instant,
// which is the whole reason both are on the row.
const START = '2026-09-07T09:00:00Z'
const END = '2026-09-07T09:45:00Z'
const ORGANIZER_START = '11:00'
const BOOKER_TIME = '18:00'
const BOOKER_ZONE = 'Asia/Tokyo'

const LINK_NAME = 'Mentorship'

const synced: Meeting = {
  id: '0199a1f0-0000-7000-8000-000000000001',
  start: START,
  end: END,
  durationMinutes: 45,
  status: 'confirmed',
  linkId: '0199a1f0-0000-7000-8000-00000000000a',
  linkName: LINK_NAME,
  participantName: 'Ada Lovelace',
  participantEmail: 'ada@example.com',
  participantTimeZone: BOOKER_ZONE,
  calendarSynced: true,
  calendarProvider: 'google',
  calendarError: null,
}

/** The provider was asked and refused. Part B recorded why; the row has to say it. */
const refused: Meeting = {
  ...synced,
  id: '0199a1f0-0000-7000-8000-000000000002',
  calendarSynced: false,
  calendarProvider: 'google',
  calendarError: 'Request had insufficient authentication scopes.',
}

/** Nobody ever connected a calendar. Not synced, and not a fault either. */
const neverTried: Meeting = {
  ...synced,
  id: '0199a1f0-0000-7000-8000-000000000003',
  calendarSynced: false,
  calendarProvider: null,
  calendarError: null,
}

function answer(meetings: Meeting[]): List {
  return {
    timeZone: ORGANIZER_ZONE,
    links: [{ id: synced.linkId, name: LINK_NAME }],
    meetings,
  }
}

/** The row a meeting was drawn into, found by the participant it names. */
async function rowFor(meeting: Meeting): Promise<HTMLElement> {
  const cell = await screen.findByText(meeting.participantName)
  const row = cell.closest('tr')
  if (row === null) throw new Error('the meeting was not drawn into a table row')
  return row
}

beforeEach(() => {
  vi.mocked(listMeetings).mockResolvedValue(answer([synced]))
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

test('every row carries the link it was booked through', async () => {
  render(<Meetings />)

  const row = await rowFor(synced)

  // The one fact the calendar cannot supply, and the reason this tab exists
  // rather than deferring to Google Calendar.
  expect(within(row).getByText(LINK_NAME).className).toContain('badge')
})

test('times are the organizer stored zone, with the booker own zone on the detail', async () => {
  render(<Meetings />)

  const row = await rowFor(synced)

  // Neither number is the machine's. The organizer's comes from the response's
  // `timeZone`, so Task 6's picker changes this page without touching it.
  expect(within(row).getByText(new RegExp(ORGANIZER_START))).toBeDefined()
  expect(within(row).getByText(new RegExp(`${BOOKER_TIME} their time`))).toBeDefined()
  expect(within(row).getByText(new RegExp(BOOKER_ZONE))).toBeDefined()
  expect(await screen.findByText(new RegExp(ORGANIZER_ZONE))).toBeDefined()
})

test('a calendar that refused reads not synced, and says why on the detail', async () => {
  vi.mocked(listMeetings).mockResolvedValue(answer([refused]))
  render(<Meetings />)

  const row = await rowFor(refused)
  const state = within(row).getByText('Not synced')

  // The second channel. `paused` leaves kairos.css's 7px dot hollow where
  // `live` fills it, so the state survives a monochrome screenshot — a red word
  // alone would not.
  expect(state.getAttribute('data-state')).toBe('paused')
  expect(state.className).toContain('status')
  // Without this the operator goes to the server log for something the row
  // already knows.
  expect(within(row).getByText(new RegExp(refused.calendarError ?? ''))).toBeDefined()
})

test('a meeting nobody could have synced says so without inventing a fault', async () => {
  vi.mocked(listMeetings).mockResolvedValue(answer([neverTried]))
  render(<Meetings />)

  const row = await rowFor(neverTried)

  expect(within(row).getByText('Not synced').getAttribute('data-state')).toBe('paused')
  expect(within(row).getByText(/No calendar was connected/)).toBeDefined()
})

test('a synced meeting fills the dot the unsynced one leaves hollow', async () => {
  render(<Meetings />)

  const row = await rowFor(synced)

  expect(within(row).getByText('Synced').getAttribute('data-state')).toBe('live')
})

test('every cell keeps its column name, which is what survives below 760px', async () => {
  render(<Meetings />)

  const row = await rowFor(synced)
  const headings = [...document.querySelectorAll('.table thead th')].map(
    (heading) => heading.textContent,
  )
  const cells = [...row.querySelectorAll('td')].map((cell) => cell.getAttribute('data-column'))

  // Below 760px kairos.css hides the heading row and reprints `data-column` in
  // front of every value. A cell without the attribute does not look wrong on a
  // laptop and is an orphaned value on a phone, which is why this asserts the
  // attribute rather than the appearance. The identity column carries it empty
  // on purpose — that is the selector that lays the row's title out as a title.
  expect(cells).toHaveLength(headings.length)
  expect(cells.every((name) => name !== null)).toBe(true)
  expect(cells.filter((name) => name === '')).toHaveLength(1)
  expect(cells.filter((name) => name !== '')).toEqual(
    headings.filter((heading) => heading !== 'Participant'),
  )
})

test('an empty list names which filter closed it', async () => {
  vi.mocked(listMeetings).mockResolvedValue(answer([]))
  render(<Meetings />)

  // Upcoming is the opening filter, so an empty answer here is a narrowed one
  // and the recovery is to widen it — not "nothing has ever been booked".
  expect(await screen.findByText(/No meetings match this link and this period/)).toBeDefined()
})

test('every class the page renders exists in kairos.css', async () => {
  vi.mocked(listMeetings).mockResolvedValue(answer([synced, refused]))
  const { container } = render(<Meetings />)
  // Both rows name the same participant, so this waits on all of them rather
  // than on one — the unsynced row's detail is half of what is being weighed.
  expect(await screen.findAllByText(synced.participantName)).toHaveLength(2)

  const css = readFileSync(stylesheet, 'utf8')
  const used = new Set(
    [...container.querySelectorAll('[class]')].flatMap((element) => [...element.classList]),
  )

  // The task adds no CSS, so a class this markup invents is a class that styles
  // nothing — and the failure is silent: a cell simply stops keeping its column
  // name, in a viewport no unit test can see.
  expect([...used].filter((name) => !css.includes(`.${name}`))).toEqual([])
})

test('the page reads the organizer zone back and settles rather than refetching forever', async () => {
  render(<Meetings />)
  await rowFor(synced)

  // The zone starts as the browser's, because the period filter needs a "today"
  // before any answer exists, and becomes the stored one when the first answer
  // arrives. At most one correcting request, and then it stops.
  await waitFor(() => {
    expect(vi.mocked(listMeetings).mock.calls.length).toBeLessThanOrEqual(2)
  })
})

/**
 * The second page asserting the same thing, and the reason there is a shared
 * `Failure` at all: four pages each deciding what a failure reads like is four
 * places for the fifth to forget.
 *
 * A 401 is deliberately not this test. It latches session.ts inside
 * `adminRequest` and AdminShell replaces itself with the locked screen, so this
 * page is unmounted rather than left drawing a table under an apology — pinned
 * in tests/locked.test.tsx, where that mechanism lives.
 */
test('a failure the shell cannot fix is reported in place, and no table under it', async () => {
  vi.mocked(listMeetings).mockRejectedValue(new ApiError(503, null, 'The database is busy.'))
  render(<Meetings />)

  expect(await screen.findByText('The database is busy.')).toBeDefined()
  expect(screen.queryByRole('table')).toBeNull()
})
