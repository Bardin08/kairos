import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { Router } from 'wouter'
import { memoryLocation } from 'wouter/memory-location'

import AdminShell from '../src/routes/admin/AdminShell'
import Invites from '../src/routes/admin/Invites'
import Links from '../src/routes/admin/Links'
import SectionNav from '../src/routes/admin/SectionNav'
import { getAlerts } from '../src/routes/admin/adminAlerts'
import { listAllInvites } from '../src/routes/admin/adminInvites'
import { listLinks } from '../src/routes/admin/adminLinks'
import { getSettings, type Settings } from '../src/routes/admin/adminSettings'

vi.mock('../src/routes/admin/adminAlerts', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/adminAlerts')>()),
  getAlerts: vi.fn(),
}))

vi.mock('../src/routes/admin/adminSettings', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/adminSettings')>()),
  getSettings: vi.fn(),
  listTimeZones: vi.fn(),
}))

vi.mock('../src/routes/admin/adminInvites', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/adminInvites')>()),
  listAllInvites: vi.fn(),
}))

vi.mock('../src/routes/admin/adminLinks', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/adminLinks')>()),
  listLinks: vi.fn(),
}))

// Same file the running app loads, and the authority on which class names exist.
const stylesheet = resolve(process.cwd(), '../backend/src/Kairos.Api/wwwroot/kairos.css')

// The shell is mounted with `nest`, so wouter's base is this and every path the
// shell and this nav write is relative to it.
const ADMIN_BASE = '/admin'

const LINKS_PATH = `${ADMIN_BASE}/links`
const INVITES_PATH = `${ADMIN_BASE}/links/invites`

const LINKS_LABEL = 'Links'
const INVITES_LABEL = 'Personal links'

const CURRENT = 'aria-current'
const PAGE = 'page'

/** The nav landmark's accessible name, and the only handle a page has on it. */
const NAV_NAME = 'Links section'

/** The header band both pages own, and the row this nav now belongs to. */
const PAGE_HEADER = 'page-header'

const settings: Settings = {
  timeZone: 'UTC',
  signedInAs: null,
  publicBaseUrl: null,
  marginMinutes: 15,
  minNoticeMinutes: 120,
  dailyCap: 4,
  startIncrementMinutes: 30,
  proposalHoldHours: 48,
  overrides: { marginMinutes: 0, minNoticeMinutes: 0, dailyCap: 0, startIncrementMinutes: 0 },
}

function renderAt(path: string, view: React.ReactElement) {
  const { hook } = memoryLocation({ path })
  return render(
    <Router hook={hook} base={ADMIN_BASE}>
      {view}
    </Router>,
  ).container
}

/** The nav on its own, at the path the shell would be showing it on. */
function renderNav(path: string) {
  return renderAt(path, <SectionNav />)
}

function destinations(container: HTMLElement) {
  return [...container.querySelectorAll('a')]
}

/** Which destination is marked as the page being shown, by its label. */
function marked(container: HTMLElement) {
  return destinations(container)
    .filter((link) => link.getAttribute(CURRENT) === PAGE)
    .map((link) => link.textContent)
}

beforeEach(() => {
  vi.mocked(getAlerts).mockResolvedValue([])
  vi.mocked(getSettings).mockResolvedValue(settings)
  vi.mocked(listAllInvites).mockResolvedValue([])
  vi.mocked(listLinks).mockResolvedValue({
    links: [],
    patterns: [],
    globals: {
      marginMinutes: settings.marginMinutes,
      minNoticeMinutes: settings.minNoticeMinutes,
      dailyCap: settings.dailyCap,
      startIncrementMinutes: settings.startIncrementMinutes,
    },
  })
})

afterEach(cleanup)

test('the section nav names both views of the Links section', () => {
  const container = renderNav(LINKS_PATH)

  expect(destinations(container).map((link) => link.textContent)).toEqual([
    LINKS_LABEL,
    INVITES_LABEL,
  ])
})

test('the links table is the marked view when the links table is the page', () => {
  const container = renderNav(LINKS_PATH)

  expect(marked(container)).toEqual([LINKS_LABEL])
})

test('the personal links are the marked view when the personal links are the page', () => {
  const container = renderNav(INVITES_PATH)

  expect(marked(container)).toEqual([INVITES_LABEL])
})

/**
 * The reason the control is built out of links rather than out of buttons that
 * call `navigate`. An href is what makes middle-click, cmd-click, open-in-new-tab
 * and copy-link-address work, and none of the four can be reimplemented on a
 * button. The hrefs are absolute here because wouter's base is prepended before
 * the attribute is written.
 */
test('each view is a real link to its own path, not a button that navigates', () => {
  const container = renderNav(LINKS_PATH)

  expect(destinations(container).map((link) => link.getAttribute('href'))).toEqual([
    LINKS_PATH,
    INVITES_PATH,
  ])
})

test('every class the section nav renders exists in kairos.css', () => {
  const css = readFileSync(stylesheet, 'utf8')
  const container = renderNav(LINKS_PATH)

  const used = new Set(
    [...container.querySelectorAll('[class]')].flatMap((element) => [...element.classList]),
  )

  const missing = [...used].filter((name) => !css.includes(`.${name}`))
  expect(missing).toEqual([])
})

/**
 * The two tests that make the feature undeletable. Everything above renders
 * `SectionNav` itself, so removing it from the pages leaves the whole suite green
 * while the switch disappears from both views — which is the one failure this
 * task exists to prevent. Rendered here rather than through `AdminShell` because
 * the shell's routing tests only see which `<h1>` arrives.
 */
test('the links table renders the section nav', async () => {
  renderAt(LINKS_PATH, <Links />)

  expect(await screen.findByRole('navigation', { name: NAV_NAME })).toBeTruthy()
})

test('the personal links render the section nav', async () => {
  renderAt(INVITES_PATH, <Invites />)

  expect(await screen.findByRole('navigation', { name: NAV_NAME })).toBeTruthy()
})

/**
 * Where it sits, not merely that it exists. The two tests above pass with the
 * nav in its own band below the heading, which is the layout this change
 * replaces — so without this one the whole point of the change is unpinned.
 */
test('the section nav sits in the page header, beside the primary action', async () => {
  renderAt(LINKS_PATH, <Links />)

  const nav = await screen.findByRole('navigation', { name: NAV_NAME })

  expect(nav.closest(`.${PAGE_HEADER}`)).toBeTruthy()
})

/**
 * Not a guard on the order of the two routes: measured against wouter 3.10, a
 * route path matches in full, so `/links` does not swallow `/links/invites` from
 * either position. What this catches is `nest` or a wildcard on `/links`, either
 * of which makes it match everything under it and draws the links table at the
 * personal-links URL — and nothing about that page looks broken when it happens.
 */
test('the invites path renders the personal links, not the links table', async () => {
  renderAt(INVITES_PATH, <AdminShell />)

  expect(await screen.findByRole('heading', { name: INVITES_LABEL })).toBeTruthy()
  expect(screen.queryByRole('heading', { name: LINKS_LABEL })).toBeNull()
})

test('the links path still renders the links table', async () => {
  renderAt(LINKS_PATH, <AdminShell />)

  expect(await screen.findByRole('heading', { name: LINKS_LABEL })).toBeTruthy()
  expect(screen.queryByRole('heading', { name: INVITES_LABEL })).toBeNull()
})
