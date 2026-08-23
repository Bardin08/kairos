// The alert channel's half of the operator's HTTP surface.
//
// Here rather than in lib/api.ts because the booking page imports lib/api.ts, so
// anything added there lands in the entry chunk and is charged to the invitee's
// bundle budget. This module is imported only from behind the lazily loaded
// admin shell, so Rollup keeps it in the admin chunk.

import { adminRequest } from './session'

/**
 * One thing that is broken, as the shell renders it.
 *
 * Every field is source-neutral: a source this build has never heard of still
 * draws a row.
 */
export type Alert = {
  /** Machine-readable, never a sentence. Copy for it lives in AlertPanel. */
  code: string
  severity: 'error' | 'warning'
  /** A provider, a queue, a recipient — or null. */
  subject: string | null
  /** ISO instant of when the thing last worked, or null. */
  since: string | null
  /**
   * A path inside this application, or null when there is nothing to press. On
   * the alert rather than a constant in the panel: the second publisher's fix is
   * not at the first one's URL.
   */
  link: string | null
  /**
   * What the copy interpolates and the fixed fields cannot hold. Always present,
   * often empty. The panel reads only the keys its own copy names, so a source
   * that adds one never breaks a screen that has not learned it.
   */
  details: Record<string, string>
}

export function getAlerts(signal?: AbortSignal): Promise<Alert[]> {
  return adminRequest<Alert[]>('/admin/alerts', { signal })
}
