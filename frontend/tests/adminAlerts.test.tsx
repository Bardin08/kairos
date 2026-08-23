import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { Router } from 'wouter'
import { memoryLocation } from 'wouter/memory-location'

import AdminShell from '../src/routes/admin/AdminShell'
import { getAlerts, type Alert } from '../src/routes/admin/adminAlerts'
import { getSettings, type Settings } from '../src/routes/admin/adminSettings'
import AlertPanel from '../src/routes/admin/AlertPanel'

vi.mock('../src/routes/admin/adminAlerts', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/adminAlerts')>()),
  getAlerts: vi.fn(),
}))

vi.mock('../src/routes/admin/adminSettings', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/adminSettings')>()),
  getSettings: vi.fn(),
  listTimeZones: vi.fn(),
}))

const RECONNECT = 'integration_reconnect_required'
const ACCOUNT = 'operator@example.com'
const INTEGRATIONS = '/integrations'
const SETTINGS = '/admin/settings'

const reconnect: Alert = {
  code: RECONNECT,
  severity: 'error',
  subject: 'google',
  since: '2026-08-19T07:41:12Z',
  link: INTEGRATIONS,
  details: { account: ACCOUNT },
}

const ADMIN_BASE = '/admin'

const settings: Settings = {
  timeZone: 'UTC',
  signedInAs: 'Vlad Bardin',
  marginMinutes: 15,
  minNoticeMinutes: 120,
  dailyCap: 4,
  startIncrementMinutes: 30,
  proposalHoldHours: 48,
  overrides: { marginMinutes: 0, minNoticeMinutes: 0, dailyCap: 0, startIncrementMinutes: 0 },
}

/** Mounted the way App mounts it, so the shell's relative paths resolve here. */
function renderShell(path: string) {
  const { hook } = memoryLocation({ path })

  return render(
    <Router hook={hook} base={ADMIN_BASE}>
      <AdminShell />
    </Router>,
  )
}

beforeEach(() => {
  vi.mocked(getSettings).mockResolvedValue(settings)
  vi.mocked(getAlerts).mockResolvedValue([])
})

afterEach(cleanup)

test('one alert draws an indicator whose accessible name carries the count', () => {
  render(<AlertPanel alerts={[reconnect]} />)

  const trigger = screen.getByRole('button')
  expect(trigger.getAttribute('aria-expanded')).toBe('false')
  expect(trigger.getAttribute('aria-label')).toContain('1')
})

test('the panel names the account and offers the way to fix it', () => {
  render(<AlertPanel alerts={[reconnect]} />)

  fireEvent.click(screen.getByRole('button'))

  expect(screen.getByText(new RegExp(ACCOUNT))).toBeDefined()
  expect(screen.getByRole('link').getAttribute('href')).toBe(INTEGRATIONS)
})

// Driven from the fixture, so a component that hardcoded /integrations fails
// here rather than in production against the second publisher.
test('the action follows the alert, not a constant in the component', () => {
  render(<AlertPanel alerts={[{ ...reconnect, link: SETTINGS }]} />)

  fireEvent.click(screen.getByRole('button'))

  expect(screen.getByRole('link').getAttribute('href')).toBe(SETTINGS)
})

test('an alert with no link renders its sentence and no action', () => {
  render(<AlertPanel alerts={[{ ...reconnect, link: null }]} />)

  fireEvent.click(screen.getByRole('button'))

  expect(screen.getByText(new RegExp(ACCOUNT))).toBeDefined()
  expect(screen.queryByRole('link')).toBeNull()
})

test('an alert with no account falls back to naming the provider', () => {
  render(<AlertPanel alerts={[{ ...reconnect, details: {} }]} />)

  fireEvent.click(screen.getByRole('button'))

  expect(screen.getByText(/google/i)).toBeDefined()
})

// A permanent bell that is empty 364 days a year is furniture, and an operator
// stops seeing furniture — which is the failure this whole task exists to fix.
test('no alerts draws nothing at all — no indicator, no empty panel', () => {
  const { container } = render(<AlertPanel alerts={[]} />)

  expect(container.querySelector('button')).toBeNull()
})

// The "every admin screen" half of the condition: the operator did not come in
// through Links, and the alert is there anyway.
test('the alert reaches a second admin screen', async () => {
  vi.mocked(getAlerts).mockResolvedValue([reconnect])

  renderShell('/admin/meetings')

  await waitFor(() =>
    expect(screen.getAllByLabelText(/problem needs attention/).length).toBeGreaterThan(0),
  )
})

// The rail is navigation, and nothing about it should turn red because a fetch
// did — least of all the control whose whole job is to be believed when it does.
test('a refused alerts read leaves the shell standing and draws no indicator', async () => {
  vi.mocked(getAlerts).mockRejectedValue(new Error('refused'))

  const { container } = renderShell('/admin/links')

  await waitFor(() => expect(screen.getAllByText('Links').length).toBeGreaterThan(0))
  expect(container.querySelector('.alertpanel')).toBeNull()
})

// The shell mounts the panel twice — the rail and the tab bar are both always
// rendered, and kairos.css picks which one is seen. A literal id would therefore
// be in the document twice, and every aria-controls pointing at it ambiguous.
test('two mounted panels do not share one element id', async () => {
  vi.mocked(getAlerts).mockResolvedValue([reconnect])

  renderShell('/admin/links')

  const triggers = await screen.findAllByLabelText(/problem needs attention/)
  expect(triggers.length).toBeGreaterThan(1)

  const targets = triggers.map((trigger) => trigger.getAttribute('aria-controls'))
  expect(new Set(targets).size).toBe(targets.length)
})
