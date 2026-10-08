import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { Router } from 'wouter'
import { memoryLocation } from 'wouter/memory-location'

import AdminShell from '../src/routes/admin/AdminShell'
import { getAlerts, type Alert } from '../src/routes/admin/adminAlerts'
import { getSettings, type Settings } from '../src/routes/admin/adminSettings'
import { DISMISSAL_KEY } from '../src/routes/admin/conditionDismissal'

vi.mock('../src/routes/admin/adminAlerts', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/adminAlerts')>()),
  getAlerts: vi.fn(),
}))

vi.mock('../src/routes/admin/adminSettings', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/adminSettings')>()),
  getSettings: vi.fn(),
  listTimeZones: vi.fn(),
}))

const ADMIN_BASE = '/admin'
const ACCOUNT = 'operator@example.com'

const reconnect: Alert = {
  code: 'integration_reconnect_required',
  severity: 'error',
  subject: 'google',
  since: '2026-08-19T07:41:12Z',
  link: '/integrations',
  details: { account: ACCOUNT },
}

const outbox: Alert = {
  code: 'outbox_stalled',
  severity: 'error',
  subject: 'outbox',
  since: null,
  link: '/admin/meetings',
  details: {},
}

const settings: Settings = {
  timeZone: 'UTC',
  signedInAs: 'Vlad Bardin',
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

function renderShell(path: string) {
  const { hook } = memoryLocation({ path })

  return render(
    <Router hook={hook} base={ADMIN_BASE}>
      <AdminShell />
    </Router>,
  )
}

const dismissControl = () => screen.getByRole('button', { name: /dismiss/i })

/** Both mounts render in jsdom; kairos.css is what hides one of them in a browser. */
const markers = () => screen.queryAllByRole('button', { name: /problem/i })

beforeEach(() => {
  localStorage.clear()
  vi.mocked(getSettings).mockResolvedValue(settings)
  vi.mocked(getAlerts).mockResolvedValue([])
})

afterEach(cleanup)

test('an undismissed condition opens as a banner, with no marker in the rail', async () => {
  vi.mocked(getAlerts).mockResolvedValue([reconnect])

  renderShell('/admin/links')

  expect(await screen.findByText(new RegExp(ACCOUNT))).toBeDefined()
  expect(markers().length).toBe(0)
})

test('dismissing collapses the banner to a counted marker', async () => {
  vi.mocked(getAlerts).mockResolvedValue([reconnect])

  renderShell('/admin/links')
  await screen.findByText(new RegExp(ACCOUNT))

  fireEvent.click(dismissControl())

  expect(screen.queryByText(new RegExp(ACCOUNT))).toBeNull()
  expect(markers().length).toBeGreaterThan(0)
  expect(markers()[0].textContent).toContain('1 problem')
})

test('pressing the marker brings the banner back', async () => {
  vi.mocked(getAlerts).mockResolvedValue([reconnect])

  renderShell('/admin/links')
  await screen.findByText(new RegExp(ACCOUNT))
  fireEvent.click(dismissControl())

  fireEvent.click(markers()[0])

  expect(screen.getByText(new RegExp(ACCOUNT))).toBeDefined()
})

test('a dismissal survives moving to another admin screen', async () => {
  vi.mocked(getAlerts).mockResolvedValue([reconnect])

  const first = renderShell('/admin/links')
  await screen.findByText(new RegExp(ACCOUNT))
  fireEvent.click(dismissControl())
  first.unmount()

  renderShell('/admin/meetings')

  await waitFor(() => expect(markers().length).toBeGreaterThan(0))
  expect(screen.queryByText(new RegExp(ACCOUNT))).toBeNull()
})

// The one that protects the operator: break this first when checking that these
// tests can fail at all.
test('a dismissal does not hide a condition that arrived after it', async () => {
  vi.mocked(getAlerts).mockResolvedValue([reconnect])

  const first = renderShell('/admin/links')
  await screen.findByText(new RegExp(ACCOUNT))
  fireEvent.click(dismissControl())
  first.unmount()

  vi.mocked(getAlerts).mockResolvedValue([reconnect, outbox])
  renderShell('/admin/links')

  expect(await screen.findByText(new RegExp(ACCOUNT))).toBeDefined()
})

test('a cleared condition leaves neither banner nor marker, and forgets the dismissal', async () => {
  vi.mocked(getAlerts).mockResolvedValue([reconnect])

  const first = renderShell('/admin/links')
  await screen.findByText(new RegExp(ACCOUNT))
  fireEvent.click(dismissControl())
  first.unmount()

  vi.mocked(getAlerts).mockResolvedValue([])
  renderShell('/admin/links')

  await waitFor(() => expect(localStorage.getItem(DISMISSAL_KEY)).toBeNull())
  expect(markers().length).toBe(0)
  expect(screen.queryByText(new RegExp(ACCOUNT))).toBeNull()
})

test('a refused alerts read leaves the shell standing and draws nothing', async () => {
  vi.mocked(getAlerts).mockRejectedValue(new Error('refused'))

  renderShell('/admin/links')

  await waitFor(() => expect(screen.getAllByText('Links').length).toBeGreaterThan(0))
  expect(markers().length).toBe(0)
})
