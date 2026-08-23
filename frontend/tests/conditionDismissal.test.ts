import { afterEach, beforeEach, expect, test, vi } from 'vitest'

import type { Alert } from '../src/routes/admin/adminAlerts'
import {
  DISMISSAL_KEY,
  dismissedFor,
  forget,
  remember,
  signatureOf,
} from '../src/routes/admin/conditionDismissal'

const RECONNECT = 'integration_reconnect_required'
const OUTBOX = 'outbox_stalled'

function alert(code: string, subject: string | null): Alert {
  return { code, severity: 'error', subject, since: null, link: '/integrations', details: {} }
}

const google = alert(RECONNECT, 'google')
const outbox = alert(OUTBOX, null)

beforeEach(() => localStorage.clear())
afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

/**
 * A storage that refuses everything, in place of the real one.
 *
 * Spying on `Storage.prototype` was tried first and silently did nothing: jsdom's
 * localStorage does not dispatch through that prototype, so the spy applied, the
 * test passed, and removing the try/catch it was meant to cover changed nothing.
 * Replacing the global is what actually reaches the code under test.
 */
function denyStorage(): void {
  vi.stubGlobal('localStorage', {
    getItem: () => {
      throw new Error('denied')
    },
    setItem: () => {
      throw new Error('denied')
    },
    removeItem: () => {
      throw new Error('denied')
    },
  })
}

// Order comes from the route's severity sort, which can change without the set
// changing. A signature that moved when the order did would reopen the banner for
// an operator who had already seen exactly these conditions.
test('the signature does not depend on the order the alerts arrive in', () => {
  expect(signatureOf([google, outbox])).toBe(signatureOf([outbox, google]))
})

test('a different alert set produces a different signature', () => {
  expect(signatureOf([google])).not.toBe(signatureOf([google, outbox]))
})

test('the same code about a different subject is a different signature', () => {
  expect(signatureOf([alert(RECONNECT, 'google')])).not.toBe(
    signatureOf([alert(RECONNECT, 'zoom')]),
  )
})

test('remember then dismissedFor reports the same set as dismissed', () => {
  remember([google])

  expect(dismissedFor([google])).toBe(true)
})

// The protection this module exists for: a dismissal must never hide a fault the
// operator has not seen.
test('a dismissal does not carry to a set that gained an alert', () => {
  remember([google])

  expect(dismissedFor([google, outbox])).toBe(false)
})

test('an empty set is never dismissed', () => {
  remember([google])

  expect(dismissedFor([])).toBe(false)
})

test('forget clears the key, so a recurrence is loud again', () => {
  remember([google])
  forget()

  expect(localStorage.getItem(DISMISSAL_KEY)).toBeNull()
  expect(dismissedFor([google])).toBe(false)
})

// A browser that refuses storage — private mode, site data blocked — must degrade
// toward being seen, never toward silence.
test('a storage that throws on read reports not dismissed', () => {
  denyStorage()

  expect(dismissedFor([google])).toBe(false)
})

test('a storage that throws on write does not propagate', () => {
  denyStorage()

  expect(() => remember([google])).not.toThrow()
})

test('a storage that throws on removal does not propagate', () => {
  denyStorage()

  expect(() => forget()).not.toThrow()
})
