// Every sentence the condition surface says, keyed by the code the server sent.
//
// Copy lives here and never on the server: `AlertResponse.Code` is documented as
// "machine-readable, and never a sentence", and every other admin surface in this
// application turns a code into words on this side. `details` supplies the values
// the sentence interpolates; this supplies the sentence around them.

import type { Alert } from './adminAlerts'

/** The one code the integrations source publishes, and the one key it documents. */
const RECONNECT_REQUIRED = 'integration_reconnect_required'
const ACCOUNT = 'account'

/** When the alert names nothing at all. Better than a sentence with a hole in it. */
const SOMETHING = 'a connected account'

export type ConditionCopy = {
  title: string
  body: string
  /** The label on the control that leads to the fix. */
  action: string
}

export function copyFor(alert: Alert): ConditionCopy {
  const who = alert.details[ACCOUNT] ?? alert.subject ?? SOMETHING

  if (alert.code === RECONNECT_REQUIRED) {
    return {
      title: `Kairos can no longer reach ${who}`,
      body: 'Bookings are still being accepted and are not reaching the calendar.',
      action: 'Reconnect',
    }
  }

  // The surface is the operator's only channel, so a code this build has never
  // heard of must not be an invisible one.
  return {
    title: `Something needs attention: ${who}`,
    body: 'This build has no wording for it yet. The link leads to where it is handled.',
    action: 'Open',
  }
}

/** `2026-08-19T07:41:12Z` → `19 Aug 2026`, or null when nothing was ever proven. */
export function lastWorked(since: string | null): string | null {
  if (since === null) return null

  return new Date(since).toLocaleDateString(undefined, {
    day: 'numeric',
    month: 'short',
    year: 'numeric',
  })
}
