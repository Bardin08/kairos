import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, expect, test } from 'vitest'
import { Router } from 'wouter'
import { memoryLocation } from 'wouter/memory-location'

import App from '../src/App'
import AdminShell from '../src/routes/admin/AdminShell'

// Same file the running app loads, read the same way stylesheet.test.ts reads
// it. Nothing in this task may add CSS, so the stylesheet is the authority on
// which class names exist.
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

afterEach(cleanup)

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
  // heavier weight, the tab bar's accent and 2px edge bar — so losing it here
  // is losing every mark on the current destination at once.
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

  // The task adds no CSS, so a class this markup invents is a class that styles
  // nothing — and the failure it produces is silent: the rail simply stops
  // collapsing, or a cell stops keeping its column name, in a viewport no unit
  // test can see.
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
