import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { Router } from 'wouter'
import { memoryLocation } from 'wouter/memory-location'

import App from '../src/App'
import AdminShell from '../src/routes/admin/AdminShell'
import { getSettings, type Settings } from '../src/routes/admin/adminSettings'

vi.mock('../src/routes/admin/adminSettings', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/adminSettings')>()),
  getSettings: vi.fn(),
  listTimeZones: vi.fn(),
}))

const OPERATOR = 'Vlad Bardin'

const settings: Settings = {
  timeZone: 'UTC',
  signedInAs: OPERATOR,
  marginMinutes: 15,
  minNoticeMinutes: 120,
  dailyCap: 4,
  startIncrementMinutes: 30,
  proposalHoldHours: 48,
  overrides: { marginMinutes: 0, minNoticeMinutes: 0, dailyCap: 0, startIncrementMinutes: 0 },
}

// Same file the running app loads, and the authority on which class names exist.
// Part C adds exactly one rule to it, for the tab bar's secondary destination.
const stylesheet = resolve(process.cwd(), '../backend/src/Kairos.Api/wwwroot/kairos.css')

const ADMIN_BASE = '/admin'

// Mounted the way App mounts it — `nest` sets this base — so the shell's
// relative paths resolve here exactly as they do in the browser.
function renderShell(path: string) {
  const { hook } = memoryLocation({ path })
  const view = render(
    <Router hook={hook} base={ADMIN_BASE}>
      <AdminShell />
    </Router>,
  )
  return view.container
}

function tabBar(container: HTMLElement) {
  const bar = container.querySelector('.tabbar')
  if (bar === null) throw new Error('no .tabbar rendered')
  return bar
}

beforeEach(() => {
  vi.mocked(getSettings).mockResolvedValue(settings)
})

afterEach(cleanup)

/**
 * The comp's identity block, restored now that OIDC supplies the claim it needs.
 * The name is whatever the provider called this operator and null when it called
 * them nothing, and null draws no block: a subject identifier or an email address
 * presented as a person's name would be worse than the gap Task 2 left here.
 */
test('the rail foot names the signed-in operator when the claims carry a name', async () => {
  renderShell('/admin/links')

  expect(await screen.findByText(OPERATOR)).toBeDefined()
  // Two letters beside it, aria-hidden, so nothing is read out twice.
  expect(screen.getByText('VB').getAttribute('aria-hidden')).toBe('true')
})

test('a provider that sends no name leaves the rail foot with no identity block', async () => {
  vi.mocked(getSettings).mockResolvedValue({ ...settings, signedInAs: null })
  const container = renderShell('/admin/links')

  // Awaited on the read itself, so the assertion is made after it settled
  // rather than before it started — there is no rendered change to wait for
  // when the answer is "draw nothing".
  await waitFor(() => expect(vi.mocked(getSettings)).toHaveBeenCalled())

  expect(container.querySelector('.avatar')).toBeNull()
})

test('the rail and the tab bar carry the same three destinations', () => {
  const container = renderShell('/admin/links')

  // Direct child: the rail's own nav, not the one in `.rail-foot` under it.
  const rail = [...container.querySelectorAll('.rail > .nav a')].map((link) => link.textContent)
  // `.tabbar-inner` is grid-template-columns: repeat(3, 1fr) in kairos.css. A
  // fourth child does not overflow it, it wraps into a second row a third of
  // the way across, so the count is a layout contract and not a preference.
  const tabs = [...tabBar(container).querySelectorAll('.tabbar-inner a')].map(
    (link) => link.textContent,
  )

  expect(rail).toEqual(['Links', 'Availability', 'Meetings'])
  expect(tabs).toEqual(['Links', 'Availability', 'Meetings'])
})

test('the current destination is marked in both the rail and the tab bar', () => {
  const container = renderShell('/admin/availability')

  const marked = [...container.querySelectorAll('[aria-current="page"]')].map(
    (link) => link.textContent,
  )

  // Twice: once in the rail, once in the tab bar. `aria-current` is the single
  // hook both of kairos.css's channels hang off — the rail's inset fill and
  // heavier weight, the tab bar's accent and 2px edge bar.
  expect(marked).toEqual(['Availability', 'Availability'])
})

test('Settings is reachable from both surfaces without taking a tab', () => {
  const container = renderShell('/admin/links')

  const foot = container.querySelector('.rail-foot a')
  const inTabBar = [...tabBar(container).querySelectorAll('a')].filter(
    (link) => link.textContent === 'Settings',
  )

  expect(foot?.textContent).toBe('Settings')
  expect(inTabBar).toHaveLength(1)
  // Present in the bar, absent from its three-column grid.
  expect(inTabBar[0].closest('.tabbar-inner')).toBeNull()
})

test('every class the shell renders exists in kairos.css', () => {
  const css = readFileSync(stylesheet, 'utf8')
  const container = renderShell('/admin/links')

  const used = new Set(
    [...container.querySelectorAll('[class]')].flatMap((element) => [...element.classList]),
  )

  // The task adds no CSS, so a class this markup invents styles nothing, and
  // fails silently: the rail stops collapsing, or a cell stops keeping its
  // column name, in a viewport no unit test can see.
  const missing = [...used].filter((name) => !css.includes(`.${name}`))
  expect(missing).toEqual([])
})

test('/admin lands on Links, and its pages mount behind the lazy boundary', async () => {
  const { hook } = memoryLocation({ path: '/admin' })
  render(
    <Router hook={hook}>
      <App />
    </Router>,
  )

  // Through App rather than through AdminShell: this is the assertion that the
  // admin chunk is reachable at all. `lazy` returns a promise, `nest` supplies
  // the base the shell's relative paths need, and getting either wrong leaves a
  // blank page that renders without throwing.
  expect(await screen.findByRole('heading', { name: 'Links' })).toBeDefined()
})
