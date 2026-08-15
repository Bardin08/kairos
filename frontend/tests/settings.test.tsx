import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'

import Settings from '../src/routes/admin/Settings'
import { ApiError } from '../src/lib/api'
import {
  getSettings,
  listTimeZones,
  saveSettings,
  type Settings as OperatorSettings,
} from '../src/routes/admin/adminSettings'

vi.mock('../src/routes/admin/adminSettings', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/adminSettings')>()),
  getSettings: vi.fn(),
  listTimeZones: vi.fn(),
  saveSettings: vi.fn(),
}))

// Same file the running app loads. This part adds exactly one rule to it, so
// the stylesheet stays the authority on which class names exist.
const stylesheet = resolve(process.cwd(), '../backend/src/Kairos.Api/wwwroot/kairos.css')

const WARSAW = 'Europe/Warsaw'
const UTC = 'UTC'

const settings: OperatorSettings = {
  timeZone: UTC,
  signedInAs: 'Vlad Bardin',
  marginMinutes: 15,
  minNoticeMinutes: 120,
  dailyCap: 4,
  startIncrementMinutes: 30,
  overrides: { marginMinutes: 3, minNoticeMinutes: 0, dailyCap: 0, startIncrementMinutes: 0 },
}

const zones = [
  { id: UTC, offset: '+00:00' },
  { id: WARSAW, offset: '+02:00' },
]

function zonePicker() {
  return screen.findByLabelText('The zone your availability is written in')
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(getSettings).mockResolvedValue(settings)
  vi.mocked(listTimeZones).mockResolvedValue(zones)
  vi.mocked(saveSettings).mockResolvedValue(settings)
  document.documentElement.removeAttribute('data-theme')
  localStorage.clear()
})

afterEach(cleanup)

/**
 * The zone list comes from the backend's own TZDB and carries what each zone
 * reads right now. `Europe/Warsaw` means nothing to somebody scanning for
 * `+02:00`, and a list assembled in the browser would be a second tz database
 * offering zones the save then refuses.
 */
test('the zone picker offers the served zones with their current offsets', async () => {
  render(<Settings />)

  const option = await screen.findByRole('option', { name: 'Europe/Warsaw · +02:00' })

  expect(option).toBeDefined()
})

test('changing the zone and saving sends the chosen id with all four globals', async () => {
  render(<Settings />)
  fireEvent.change(await zonePicker(), { target: { value: WARSAW } })
  fireEvent.click(screen.getByRole('button', { name: 'Save' }))

  await waitFor(() =>
    expect(vi.mocked(saveSettings)).toHaveBeenCalledWith({
      timeZoneId: WARSAW,
      marginMinutes: 15,
      minNoticeMinutes: 120,
      dailyCap: 4,
      startIncrementMinutes: 30,
    }),
  )
})

/**
 * An operator changing a global has to know how many links will ignore it. A
 * change that appears to do nothing is the one that gets made twice.
 */
test('a global says how many links override it, and stays silent when none do', async () => {
  render(<Settings />)

  expect(await screen.findByText(/3 links set their own/)).toBeDefined()
  expect(screen.queryByText(/0 links set their own/)).toBeNull()
})

/**
 * The floor that is not merely tidy: a zero start increment makes the slot
 * generator's minute step never advance. The backend refuses it; this is the
 * same rule said early enough that nothing is submitted to learn it.
 */
test('a start increment of zero is refused here rather than by the server', async () => {
  render(<Settings />)

  fireEvent.change(await screen.findByLabelText('Start increment'), { target: { value: '0' } })
  fireEvent.click(screen.getByRole('button', { name: 'Save' }))

  expect(screen.getByRole('button', { name: 'Save' }).getAttribute('aria-disabled')).toBe('true')
  expect(vi.mocked(saveSettings)).not.toHaveBeenCalled()
})

/**
 * Three states, not two.
 *
 * `system` is the default and the one most people should stay on, and it is
 * exactly the state a two-state icon toggle cannot express — so what is pinned
 * is that light and dark override the device in both directions AND that there
 * is still a way back to letting the device decide.
 */
test('the theme control forces light and dark, and hands control back to the system', async () => {
  render(<Settings />)
  const theme = await screen.findByLabelText('Theme')

  fireEvent.change(theme, { target: { value: 'dark' } })
  expect(document.documentElement.getAttribute('data-theme')).toBe('dark')

  fireEvent.change(theme, { target: { value: 'light' } })
  expect(document.documentElement.getAttribute('data-theme')).toBe('light')

  fireEvent.change(theme, { target: { value: 'system' } })
  // No attribute at all, which is what leaves prefers-color-scheme in charge.
  expect(document.documentElement.hasAttribute('data-theme')).toBe(false)
})

/**
 * `GET /admin/logout` shipped in Task 5 with no caller at all — dead code until
 * something pressed it. It lives here rather than in the rail because the rail
 * is desktop-only: below 900px it is display:none, and a sign-out a phone
 * cannot reach is barely better than none.
 */
test('the session can be ended, through a plain anchor and not a router link', async () => {
  render(<Settings />)

  const signOut = await screen.findByRole('link', { name: 'Sign out' })

  // /admin/logout is a SERVER route under the path the single page nests on. A
  // wouter <Link> here is swallowed by the router and renders its not-found.
  expect(signOut.getAttribute('href')).toBe('/admin/logout')
})

/**
 * An expired cookie on a workbench left open overnight is the most ordinary
 * failure this surface has, and rendered generically it reads "The server
 * answered 401." — a status code, and no way out of it.
 */
test('an expired session is a way back in rather than a status code', async () => {
  vi.mocked(getSettings).mockRejectedValue(new ApiError(401, null, 'Unauthorized'))
  render(<Settings />)

  const signIn = await screen.findByRole('link', { name: 'Sign in again' })

  expect(signIn.getAttribute('href')).toBe('/admin/login')
  expect(screen.queryByText(/The server answered/)).toBeNull()
})

test('every class this page renders exists in kairos.css', async () => {
  const css = readFileSync(stylesheet, 'utf8')
  const { container } = render(<Settings />)
  await screen.findByLabelText('Theme')

  const used = new Set(
    [...container.querySelectorAll('[class]')].flatMap((element) => [...element.classList]),
  )

  // A class this markup invents is a class that styles nothing, and the failure
  // it produces is silent: a control simply stops keeping its shape, in a
  // viewport no unit test can see.
  const missing = [...used].filter((name) => !css.includes(`.${name}`))
  expect(missing).toEqual([])
})
