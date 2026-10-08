import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'

import Availability from '../src/routes/admin/Availability'
// ApiError comes from the real module and is never mocked: the page reads a
// failure through `asApiError`, which recognises it with `instanceof`. A plain
// object would take the "carries no status" path and every message these tests
// look for would be replaced with "could not reach the server".
import { ApiError } from '../src/lib/api'
import {
  createPattern,
  deletePattern,
  listPatterns,
  type Pattern,
} from '../src/routes/admin/patterns'
import { getSettings, type Settings } from '../src/routes/admin/adminSettings'

vi.mock('../src/routes/admin/patterns', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/patterns')>()),
  listPatterns: vi.fn(),
  createPattern: vi.fn(),
  deletePattern: vi.fn(),
}))

vi.mock('../src/routes/admin/adminSettings', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/adminSettings')>()),
  getSettings: vi.fn(),
}))

// Same file the running app loads. The task adds no CSS, so the stylesheet is
// the authority on which class names exist.
const stylesheet = resolve(process.cwd(), '../backend/src/Kairos.Api/wwwroot/kairos.css')

const CONFLICT = 409
const MONDAY = 1
const NINE_AM = 9 * 60
const FIVE_PM = 17 * 60

const weekdays: Pattern = {
  id: 'b0a5f0e2-0000-7000-8000-000000000001',
  name: 'Weekdays 09–17',
  rules: [{ id: 'r1', isoDayOfWeek: MONDAY, startMinute: NINE_AM, endMinute: FIVE_PM }],
  overrides: [],
  usedBy: ['Mentorship'],
}

const ORGANIZER_ZONE = 'Europe/Warsaw'

const settings: Settings = {
  timeZone: ORGANIZER_ZONE,
  signedInAs: null,
  publicBaseUrl: null,
  marginMinutes: 15,
  minNoticeMinutes: 120,
  dailyCap: 4,
  startIncrementMinutes: 30,
  proposalHoldHours: 48,
  operatorName: null,
  contactEmail: null,
  jurisdiction: null,
  overrides: { marginMinutes: 0, minNoticeMinutes: 0, dailyCap: 0, startIncrementMinutes: 0 },
}

beforeEach(() => {
  vi.mocked(listPatterns).mockResolvedValue([weekdays])
  vi.mocked(createPattern).mockResolvedValue(weekdays)
  vi.mocked(getSettings).mockResolvedValue(settings)
})

afterEach(cleanup)

/**
 * The hours on a card carry the zone they are read in. A card is the unit
 * somebody screenshots, quotes and argues with, and 09:00–17:00 with no zone on
 * it is a time that means four different things.
 */
test('a pattern card names the organizer zone beside its hours', async () => {
  render(<Availability />)

  expect(await screen.findByText(/09:00–17:00 · Europe\/Warsaw/)).toBeDefined()
})

/**
 * The path is relative on purpose: the shell is mounted with `nest`, so wouter
 * prefixes `/admin`, and an absolute href here would resolve to
 * `/admin/admin/availability/…`.
 */
test('a card leads to that pattern’s editor', async () => {
  render(<Availability />)

  const edit = await screen.findByRole('link', { name: 'Edit' })

  expect(edit.getAttribute('href')).toBe(`/availability/${weekdays.id}`)
})

/**
 * Naming a new pattern is a field in a card, not a naked input in the header.
 * The header holds a heading and one control, here as on every other screen.
 */
test('the header button opens a name field and creates from it', async () => {
  render(<Availability />)

  fireEvent.click(await screen.findByRole('button', { name: 'New pattern' }))
  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Evenings' } })
  fireEvent.click(screen.getByRole('button', { name: 'Create pattern' }))

  await waitFor(() => expect(vi.mocked(createPattern)).toHaveBeenCalledWith('Evenings'))
})

/**
 * The refusal is shown, and shown verbatim. The server's sentence is the only one
 * with the link names in it, so a friendlier message of this page's own would
 * delete the only part the operator can act on — which link to go and repoint.
 */
test('a refused delete is shown with the links the server named', async () => {
  vi.mocked(deletePattern).mockRejectedValue(
    new ApiError(
      CONFLICT,
      'pattern_in_use',
      'This pattern is the availability of Mentorship. Point that link at another pattern first.',
    ),
  )

  render(<Availability />)
  fireEvent.click(await screen.findByRole('button', { name: 'Delete' }))

  expect((await screen.findByRole('alert')).textContent).toContain('Mentorship')
  // Still there. A refusal that removed the card anyway would report success
  // for a delete that did not happen.
  expect(screen.getByRole('heading', { name: weekdays.name })).toBeDefined()
})

/**
 * No CSS was added by this task, so a class this markup invents styles nothing,
 * and fails silently: the card stops being a card, in a viewport no unit test can
 * see. The name form is opened first, being the one part not drawn on arrival.
 */
test('every class the page renders exists in kairos.css', async () => {
  const css = readFileSync(stylesheet, 'utf8')
  const { container } = render(<Availability />)
  fireEvent.click(await screen.findByRole('button', { name: 'New pattern' }))

  const used = new Set(
    [...container.querySelectorAll('[class]')].flatMap((element) => [...element.classList]),
  )

  expect([...used].filter((name) => !css.includes(`.${name}`))).toEqual([])
})
