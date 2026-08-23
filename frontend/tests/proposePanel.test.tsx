import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, test, vi } from 'vitest'

import ProposePanel from '../src/routes/admin/ProposePanel'
import { ApiError, getSlots } from '../src/lib/api'
import { proposeMeeting } from '../src/routes/admin/adminMeetings'
import type { Meeting } from '../src/routes/admin/adminMeetings'

// The slot read is the PUBLIC one, in lib/api rather than beside the admin
// calls: an operator picking a time and an invitee picking one are the same act
// against the same generator, and a second endpoint for the operator would be a
// second answer to what a link offers. ApiError stays real — `Failure` reads a
// rejection through `asApiError`, which branches on `instanceof`.
vi.mock('../src/lib/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/lib/api')>()),
  getSlots: vi.fn(),
}))

vi.mock('../src/routes/admin/adminMeetings', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/adminMeetings')>()),
  proposeMeeting: vi.fn(),
}))

// Same file the running app loads, and the authority on which class names exist.
const stylesheet = resolve(process.cwd(), '../backend/src/Kairos.Api/wwwroot/kairos.css')

const SLUG = 'consulting'
const DURATION_MINUTES = 30

// The ORGANIZER's, and deliberately not the zone this machine is in.
const ORGANIZER_ZONE = 'Europe/Warsaw'

// September, so Warsaw is +02:00. Every date here is inside the month the
// picker opens on, which is the month containing TODAY.
const TODAY = '2030-09-01T08:00:00Z'
const PROPOSED_SLOT = '2030-09-09T14:00:00Z'
const PROPOSED_TIME = '16:00'
const OTHER_SLOT = '2030-09-09T15:00:00Z'

const CONFLICT = 409
const SERVICE_UNAVAILABLE = 503

/** The server's own sentence for a read that never arrived. */
const SLOTS_UNREACHABLE_TEXT = 'The database is busy.'

// MeetingsAdminEndpoints.MailUnconfiguredCode's own sentence, shortened. A
// proposal is a question, and the only channel that can carry it is the mail.
const MAIL_UNCONFIGURED_TEXT = 'A new time cannot be suggested until Email is configured.'

const meeting: Meeting = {
  id: '0192f000-0000-7000-8000-000000000001',
  start: '2030-09-05T09:00:00Z',
  end: '2030-09-05T09:30:00Z',
  durationMinutes: DURATION_MINUTES,
  status: 'confirmed',
  linkId: '0192f000-0000-7000-8000-00000000000a',
  linkName: 'Consulting call',
  slug: SLUG,
  participantName: 'Ada Lovelace',
  participantEmail: 'ada@example.com',
  participantTimeZone: 'Asia/Tokyo',
  calendarSynced: true,
  calendarProvider: 'google',
  calendarError: null,
  proposal: null,
}

function renderPanel(onProposed = vi.fn(), onCancel = vi.fn()) {
  render(
    <ProposePanel
      meeting={meeting}
      zone={ORGANIZER_ZONE}
      onProposed={onProposed}
      onCancel={onCancel}
    />,
  )
  return { onProposed, onCancel }
}

describe('ProposePanel', () => {
  beforeEach(() => {
    // Only Date is faked, so `waitFor` still runs on real timers. The picker
    // opens on today's month in the ORGANIZER's zone, and the slots below have
    // to land inside it — the same arrangement manage.test.tsx makes.
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date(TODAY))
    vi.mocked(getSlots).mockResolvedValue([PROPOSED_SLOT, OTHER_SLOT])
  })

  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
    vi.useRealTimers()
  })

  test("reads the link's own slots, keyed on the slug and the meeting length", async () => {
    renderPanel()

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
    const { onProposed } = renderPanel()

    fireEvent.click(await screen.findByRole('button', { name: new RegExp(PROPOSED_TIME) }))
    fireEvent.click(screen.getByRole('button', { name: /suggest this time/i }))

    // The chosen string goes back exactly as it arrived. Rebuilt as an offset
    // form it is refused as instant_invalid.
    await waitFor(() => expect(proposeMeeting).toHaveBeenCalledWith(meeting.id, PROPOSED_SLOT))
    await waitFor(() => expect(onProposed).toHaveBeenCalled())
  })

  test('nothing is suggested until a time is picked', async () => {
    renderPanel()
    await screen.findByRole('button', { name: new RegExp(PROPOSED_TIME) })

    // The commit is absent rather than disabled: a button that cannot be
    // pressed still reads as an action, and there is no action here yet.
    expect(screen.queryByRole('button', { name: /suggest this time/i })).toBeNull()
    expect(proposeMeeting).not.toHaveBeenCalled()
  })

  test("a refused proposal keeps the panel open with the server's own sentence", async () => {
    vi.mocked(proposeMeeting).mockRejectedValue(
      new ApiError(CONFLICT, 'mail_unconfigured', MAIL_UNCONFIGURED_TEXT),
    )
    renderPanel()

    fireEvent.click(await screen.findByRole('button', { name: new RegExp(PROPOSED_TIME) }))
    fireEvent.click(screen.getByRole('button', { name: /suggest this time/i }))

    expect((await screen.findByRole('alert')).textContent).toContain(MAIL_UNCONFIGURED_TEXT)
    // Still armed, and the choice still standing: nothing was held, so sending
    // the operator back to pick the same time again would be busywork.
    expect(screen.queryByRole('button', { name: /suggest this time/i })).not.toBeNull()
  })

  test('backing out asks the page for the list again and proposes nothing', async () => {
    const { onCancel } = renderPanel()
    await screen.findByRole('button', { name: new RegExp(PROPOSED_TIME) })

    fireEvent.click(screen.getByRole('button', { name: /back/i }))

    expect(onCancel).toHaveBeenCalled()
    expect(proposeMeeting).not.toHaveBeenCalled()
  })

  test('a slots read that failed does not read as a month with no room in it', async () => {
    vi.mocked(getSlots).mockRejectedValue(
      new ApiError(SERVICE_UNAVAILABLE, null, SLOTS_UNREACHABLE_TEXT),
    )
    renderPanel()

    expect((await screen.findByRole('alert')).textContent).toContain(SLOTS_UNREACHABLE_TEXT)

    // The empty state's sentence is a claim about the calendar. Said over a
    // dropped connection it tells the operator this link is full when nothing
    // at all is known about it.
    expect(screen.queryByText(/has a gap this meeting fits into/i)).toBeNull()
    expect(screen.queryByRole('button', { name: /suggest this time/i })).toBeNull()
  })

  test('a month with genuinely nothing in it still says so', async () => {
    vi.mocked(getSlots).mockResolvedValue([])
    renderPanel()

    // The other half of the pair: an empty answer IS an answer, and the panel
    // has to keep saying so or the fix above would have swallowed it.
    expect(await screen.findByText(/has a gap this meeting fits into/i)).toBeDefined()
    expect(screen.queryByRole('alert')).toBeNull()
  })

  test('every class the picker renders exists in kairos.css', async () => {
    const css = readFileSync(stylesheet, 'utf8')
    const { container } = render(
      <ProposePanel
        meeting={meeting}
        zone={ORGANIZER_ZONE}
        onProposed={vi.fn()}
        onCancel={vi.fn()}
      />,
    )
    fireEvent.click(await screen.findByRole('button', { name: new RegExp(PROPOSED_TIME) }))

    const used = new Set(
      [...container.querySelectorAll('[class]')].flatMap((element) => [...element.classList]),
    )

    // This task adds no CSS, so a class this markup invents is a class that
    // styles nothing — and the failure is silent: the picker simply stops
    // keeping its shape, in a viewport no unit test can see.
    expect([...used].filter((name) => !css.includes(`.${name}`))).toEqual([])
  })
})
