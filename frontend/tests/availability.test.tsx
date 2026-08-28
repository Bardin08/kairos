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
  renamePattern,
  type Pattern,
} from '../src/routes/admin/patterns'
import { getSettings, type Settings } from '../src/routes/admin/adminSettings'

vi.mock('../src/routes/admin/patterns', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/patterns')>()),
  listPatterns: vi.fn(),
  createPattern: vi.fn(),
  deletePattern: vi.fn(),
  renamePattern: vi.fn(),
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
  overrides: { marginMinutes: 0, minNoticeMinutes: 0, dailyCap: 0, startIncrementMinutes: 0 },
}

beforeEach(() => {
  vi.mocked(listPatterns).mockResolvedValue([weekdays])
  vi.mocked(createPattern).mockResolvedValue(weekdays)
  vi.mocked(getSettings).mockResolvedValue(settings)
  // Reset rather than re-stubbed: the rename tests assert that it was NOT
  // called, and nothing here clears call history between tests, so a call made
  // by the test above would be read as a call made by the test below.
  vi.mocked(renamePattern).mockReset()
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

const RENAMED = 'Weekday mornings'

/** Opens the editor on the only card the fixture draws. */
async function openRenameEditor() {
  fireEvent.click(await screen.findByRole('button', { name: weekdays.name }))
  return screen.getByRole('textbox', { name: 'Pattern name' })
}

test('clicking the name opens an editor holding the current name', async () => {
  render(<Availability />)

  expect(await openRenameEditor()).toHaveProperty('value', weekdays.name)
})

test('saving renames the pattern', async () => {
  vi.mocked(renamePattern).mockResolvedValue({ ...weekdays, name: RENAMED })
  render(<Availability />)

  const field = await openRenameEditor()
  fireEvent.change(field, { target: { value: RENAMED } })
  fireEvent.click(screen.getByRole('button', { name: 'Save' }))

  await waitFor(() => expect(vi.mocked(renamePattern)).toHaveBeenCalledWith(weekdays.id, RENAMED))
  expect(await screen.findByRole('button', { name: RENAMED })).toBeDefined()
})

test('cancelling reverts the name and writes nothing', async () => {
  render(<Availability />)

  const field = await openRenameEditor()
  fireEvent.change(field, { target: { value: RENAMED } })
  fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))

  expect(vi.mocked(renamePattern)).not.toHaveBeenCalled()
  expect(await screen.findByRole('button', { name: weekdays.name })).toBeDefined()
})

/**
 * The property this editor has and a blur-to-save one does not. A stray click
 * elsewhere on the page must not commit a half-typed name, so blur neither saves
 * nor closes — the operator chooses, every time.
 */
test('clicking away neither saves nor closes the editor', async () => {
  render(<Availability />)

  const field = await openRenameEditor()
  fireEvent.change(field, { target: { value: RENAMED } })
  fireEvent.blur(field)

  expect(vi.mocked(renamePattern)).not.toHaveBeenCalled()
  expect(screen.getByRole('textbox', { name: 'Pattern name' })).toHaveProperty('value', RENAMED)
})

/**
 * A name the server refuses leaves the editor open with the typed name still in
 * it. Closing it would throw away what the operator wrote to show them a message
 * about it.
 */
test('a refused rename keeps the editor open and shows the reason', async () => {
  vi.mocked(renamePattern).mockRejectedValue(
    new ApiError(CONFLICT, 'pattern_name_taken', 'Another pattern is already called that.'),
  )
  render(<Availability />)

  const field = await openRenameEditor()
  fireEvent.change(field, { target: { value: RENAMED } })
  fireEvent.click(screen.getByRole('button', { name: 'Save' }))

  expect((await screen.findByRole('alert')).textContent).toContain('already called that')
  expect(screen.getByRole('textbox', { name: 'Pattern name' })).toHaveProperty('value', RENAMED)
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
