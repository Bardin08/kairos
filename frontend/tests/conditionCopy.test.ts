import { expect, test } from 'vitest'

import type { Alert } from '../src/routes/admin/adminAlerts'
import { copyFor, lastWorked } from '../src/routes/admin/conditionCopy'

const ACCOUNT = 'operator@example.com'

function alert(overrides: Partial<Alert> = {}): Alert {
  return {
    code: 'integration_reconnect_required',
    severity: 'error',
    subject: 'google',
    since: '2026-08-19T07:41:12Z',
    link: '/integrations',
    details: { account: ACCOUNT },
    ...overrides,
  }
}

test('the reconnect copy names the account it cannot reach', () => {
  const copy = copyFor(alert())

  expect(copy.title).toContain(ACCOUNT)
  expect(copy.body).toContain('still being accepted')
  expect(copy.action).toBe('Reconnect')
})

test('with no account detail it falls back to the provider', () => {
  expect(copyFor(alert({ details: {} })).title).toContain('google')
})

test('an unknown code still produces a title and keeps the subject', () => {
  const copy = copyFor(alert({ code: 'something_new', details: {} }))

  expect(copy.title.length).toBeGreaterThan(0)
  expect(copy.title).toContain('google')
})

test('lastWorked formats the instant, and answers null for never', () => {
  expect(lastWorked('2026-08-19T07:41:12Z')).toContain('2026')
  expect(lastWorked(null)).toBeNull()
})
