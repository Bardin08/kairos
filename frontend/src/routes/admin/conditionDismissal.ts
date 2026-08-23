// Whether the operator has already seen the conditions currently standing, and
// therefore whether the surface opens loud or collapsed.
//
// Per browser rather than per account, and deliberately: this is loud-versus-quiet,
// the same kind of preference as the theme control, which Settings already
// describes as "a preference of this browser and is not saved to the server". A
// server-side dismissal would need a route, a column and an answer to what
// "dismissed" means across two browsers, to buy nothing an operator would notice.

import type { Alert } from './adminAlerts'

/** Exported so a test can assert the key is actually cleared rather than shadowed. */
export const DISMISSAL_KEY = 'kairos.conditions.dismissed'

/** Between an alert's code and its subject, and between alerts. */
const FIELD = ':'
const RECORD = '|'

/**
 * What "these same conditions" means, as one string.
 *
 * Sorted, because the route orders by severity and that order can change without
 * the set changing — a signature that moved with it would reopen the banner for an
 * operator who had already seen exactly these conditions. Subject is part of it:
 * the same fault about a second provider is a fault nobody has seen yet.
 */
export function signatureOf(alerts: Alert[]): string {
  return alerts
    .map((alert) => `${alert.code}${FIELD}${alert.subject ?? ''}`)
    .sort()
    .join(RECORD)
}

/**
 * Every read and write is wrapped, and every catch resolves toward the loud state.
 * A browser that refuses storage throws here — private mode is the common one —
 * and that failure must not be silence.
 */
export function dismissedFor(alerts: Alert[]): boolean {
  if (alerts.length === 0) return false

  try {
    return localStorage.getItem(DISMISSAL_KEY) === signatureOf(alerts)
  } catch {
    return false
  }
}

export function remember(alerts: Alert[]): void {
  try {
    localStorage.setItem(DISMISSAL_KEY, signatureOf(alerts))
  } catch {
    // Nothing to do and nothing to report: the next render is loud, which is the
    // safe direction to fail in.
  }
}

/**
 * Called when the alert list comes back empty, so a condition that clears and
 * later returns is loud again rather than silently matching a stale signature.
 */
export function forget(): void {
  try {
    localStorage.removeItem(DISMISSAL_KEY)
  } catch {
    // Same reasoning as remember().
  }
}
