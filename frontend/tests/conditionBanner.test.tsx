import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'

import type { Alert } from '../src/routes/admin/adminAlerts'
import ConditionBanner from '../src/routes/admin/ConditionBanner'

const ACCOUNT = 'operator@example.com'
const INTEGRATIONS = '/integrations'
const SETTINGS = '/admin/settings'

function alert(overrides: Partial<Alert> = {}): Alert {
  return {
    code: 'integration_reconnect_required',
    severity: 'error',
    subject: 'google',
    since: '2026-08-19T07:41:12Z',
    link: INTEGRATIONS,
    details: { account: ACCOUNT },
    ...overrides,
  }
}

afterEach(cleanup)

test('the banner states the account, the consequence and when it last worked', () => {
  render(<ConditionBanner alerts={[alert()]} onDismiss={() => {}} />)

  expect(screen.getByText(new RegExp(ACCOUNT))).toBeDefined()
  expect(screen.getByText(/still being accepted/)).toBeDefined()
  expect(screen.getByText(/Last worked/)).toBeDefined()
})

test('an alert that never worked shows no last-worked line', () => {
  render(<ConditionBanner alerts={[alert({ since: null })]} onDismiss={() => {}} />)

  expect(screen.queryByText(/Last worked/)).toBeNull()
})

// Driven from the fixture, so a component that hardcoded /integrations fails here
// rather than in production against the second publisher.
test('the action follows the alert, not a constant in the component', () => {
  render(<ConditionBanner alerts={[alert({ link: SETTINGS })]} onDismiss={() => {}} />)

  expect(screen.getByRole('link').getAttribute('href')).toBe(SETTINGS)
})

test('an alert with no link renders its sentence and no action', () => {
  render(<ConditionBanner alerts={[alert({ link: null })]} onDismiss={() => {}} />)

  expect(screen.getByText(new RegExp(ACCOUNT))).toBeDefined()
  expect(screen.queryByRole('link')).toBeNull()
})

test('two alerts render two banners', () => {
  render(
    <ConditionBanner
      alerts={[alert(), alert({ code: 'outbox_stalled', subject: 'outbox', details: {} })]}
      onDismiss={() => {}}
    />,
  )

  expect(screen.getAllByRole('status').length).toBe(2)
})

test('dismiss calls back, and its name says what it leaves behind', () => {
  const onDismiss = vi.fn()
  render(<ConditionBanner alerts={[alert()]} onDismiss={onDismiss} />)

  const dismiss = screen.getByRole('button', { name: /dismiss/i })
  expect(dismiss.getAttribute('aria-label')).toMatch(/sidebar/i)

  fireEvent.click(dismiss)
  expect(onDismiss).toHaveBeenCalledTimes(1)
})

test('no alerts renders nothing', () => {
  const { container } = render(<ConditionBanner alerts={[]} onDismiss={() => {}} />)

  expect(container.querySelector('.banner')).toBeNull()
})
