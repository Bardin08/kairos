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
  deletePattern,
  listPatterns,
  savePatternOverride,
  type Pattern,
} from '../src/routes/admin/patterns'

vi.mock('../src/routes/admin/patterns', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/patterns')>()),
  listPatterns: vi.fn(),
  deletePattern: vi.fn(),
  savePatternOverride: vi.fn(),
}))

// Same file the running app loads. The task adds no CSS, so the stylesheet is
// the authority on which class names exist.
const stylesheet = resolve(process.cwd(), '../backend/src/Kairos.Api/wwwroot/kairos.css')

const CONFLICT = 409
const MONDAY = 1
const NINE_AM = 9 * 60
const FIVE_PM = 17 * 60
const HOLIDAY = '2026-12-25'

const weekdays: Pattern = {
  id: 'b0a5f0e2-0000-7000-8000-000000000001',
  name: 'Weekdays 09–17',
  rules: [{ id: 'r1', isoDayOfWeek: MONDAY, startMinute: NINE_AM, endMinute: FIVE_PM }],
  overrides: [],
  usedBy: ['Mentorship'],
}

async function openEditor() {
  render(<Availability />)
  fireEvent.click(await screen.findByRole('button', { name: 'Edit' }))
}

beforeEach(() => {
  vi.mocked(listPatterns).mockResolvedValue([weekdays])
  vi.mocked(savePatternOverride).mockResolvedValue(weekdays)
})

afterEach(cleanup)

/**
 * The case a form that only offers "change the hours" cannot express.
 *
 * A blocked date is how a holiday is taken, and it is the one an operator
 * reaches for most. What is pinned is the request: both minutes null, sent
 * together, without either time control having been filled in — because that
 * is what the endpoint reads as "closed" rather than as a half-filled form.
 */
test('a date can be blocked outright, not only given different hours', async () => {
  await openEditor()

  fireEvent.change(screen.getByLabelText('Date'), { target: { value: HOLIDAY } })
  fireEvent.click(screen.getByRole('button', { name: 'Set date' }))

  await waitFor(() =>
    expect(vi.mocked(savePatternOverride)).toHaveBeenCalledWith(weekdays.id, HOLIDAY, {
      startMinute: null,
      endMinute: null,
    }),
  )
})

/** And the other half of that choice, which the same control has to reach. */
test('the same date form can replace the hours instead', async () => {
  await openEditor()

  fireEvent.change(screen.getByLabelText('Date'), { target: { value: HOLIDAY } })
  fireEvent.click(screen.getByRole('button', { name: 'Different hours' }))
  fireEvent.change(screen.getByLabelText('Opens at on this date'), { target: { value: '10:00' } })
  fireEvent.click(screen.getByRole('button', { name: 'Set date' }))

  await waitFor(() =>
    expect(vi.mocked(savePatternOverride)).toHaveBeenCalledWith(weekdays.id, HOLIDAY, {
      startMinute: 10 * 60,
      endMinute: FIVE_PM,
    }),
  )
})

/**
 * The refusal is shown, and shown verbatim.
 *
 * The server's sentence is the only one with the link names in it, so replacing
 * it with a friendlier message of this page's own would delete the only part
 * the operator can act on — which link to go and repoint.
 */
test('a refused delete is shown with the links the server named', async () => {
  vi.mocked(deletePattern).mockRejectedValue(
    new ApiError(
      CONFLICT,
      'pattern_in_use',
      'This pattern is the availability of Mentorship. Point that link at another pattern first.',
    ),
  )

  await openEditor()
  fireEvent.click(screen.getByRole('button', { name: 'Delete pattern' }))

  expect((await screen.findByRole('alert')).textContent).toContain('Mentorship')
  // Still there. A refusal that removed the card anyway would report success
  // for a delete that did not happen.
  expect(screen.getByRole('heading', { name: weekdays.name })).toBeDefined()
})

/**
 * No CSS was added by this task, so a class this markup invents is a class that
 * styles nothing — and it fails silently: the card simply stops being a card,
 * in a viewport no unit test can see. The editor is opened first, because half
 * of what this page renders is behind that button.
 */
test('every class the page renders exists in kairos.css', async () => {
  const css = readFileSync(stylesheet, 'utf8')
  const { container } = render(<Availability />)
  fireEvent.click(await screen.findByRole('button', { name: 'Edit' }))

  const used = new Set(
    [...container.querySelectorAll('[class]')].flatMap((element) => [...element.classList]),
  )

  expect([...used].filter((name) => !css.includes(`.${name}`))).toEqual([])
})
