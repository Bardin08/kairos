// The availability half of the operator's HTTP surface.
//
// Here rather than in lib/api.ts, and the reason is measured rather than
// stylistic: lib/api.ts is imported by the booking page, so anything added to
// it lands in the entry chunk and is downloaded by every invitee who opens a
// link on their phone — 240 gzipped bytes of endpoints they can never call.
// This module is imported only by Availability.tsx, which is only reachable
// through the lazily imported admin shell, so Rollup places it in the admin
// chunk. It still makes no fetch of its own: `request` is the one place a
// failure becomes an ApiError, and a second copy of that is the copy that gets
// it wrong.

import { request, requestNothing } from '../../lib/api'

/** One weekly window. Minutes from local midnight, in the ORGANIZER's zone. */
export type PatternRule = {
  id: string
  /** ISO-8601: 1 is Monday, 7 is Sunday. Not `Date.getDay()`, whose Sunday is 0. */
  isoDayOfWeek: number
  startMinute: number
  endMinute: number
}

/**
 * What one date does instead of the weekly rules.
 *
 * Both minutes null is not missing data — it is the day being blocked
 * entirely, which is how a holiday is expressed. A UI that reads null as "not
 * loaded yet" turns a deliberate closure into a rendering accident.
 */
export type PatternOverride = {
  /** A date key, `2026-12-25`. No time, so no zone of its own. */
  date: string
  startMinute: number | null
  endMinute: number | null
}

/** A named, reusable availability shape, and the links standing on it. */
export type Pattern = {
  id: string
  name: string
  rules: PatternRule[]
  overrides: PatternOverride[]
  /** Link names. Deleting a pattern any link uses is refused, naming them. */
  usedBy: string[]
}

const PATTERNS = '/admin/patterns'
const JSON_HEADERS = { 'Content-Type': 'application/json' }

function patternUrl(patternId: string): string {
  return `${PATTERNS}/${encodeURIComponent(patternId)}`
}

/**
 * A write that answers with the whole pattern, which every one of them does.
 * The caller replaces its copy rather than merging a partial response into it.
 */
function write(method: string, path: string, body?: unknown): Promise<Pattern> {
  return request<Pattern>(path, {
    method,
    headers: body === undefined ? undefined : JSON_HEADERS,
    body: body === undefined ? undefined : JSON.stringify(body),
  })
}

export function listPatterns(signal?: AbortSignal): Promise<Pattern[]> {
  return request<Pattern[]>(PATTERNS, { signal })
}

export function createPattern(name: string): Promise<Pattern> {
  return write('POST', PATTERNS, { name })
}

export function renamePattern(patternId: string, name: string): Promise<Pattern> {
  return write('PATCH', patternUrl(patternId), { name })
}

/**
 * 204, and refused with 409 while any link stands on the pattern. The refusal
 * names those links in its message, so it is shown verbatim rather than
 * replaced with a sentence of this app's own.
 */
export function deletePattern(patternId: string): Promise<void> {
  return requestNothing(patternUrl(patternId), { method: 'DELETE' })
}

export function addPatternRule(
  patternId: string,
  rule: { isoDayOfWeek: number; startMinute: number; endMinute: number },
): Promise<Pattern> {
  return write('POST', `${patternUrl(patternId)}/rules`, rule)
}

export function removePatternRule(patternId: string, ruleId: string): Promise<Pattern> {
  return write('DELETE', `${patternUrl(patternId)}/rules/${encodeURIComponent(ruleId)}`)
}

/**
 * Upsert, keyed on the date. Both minutes null blocks the date; a pair of them
 * replaces the day's hours. Those are different requests to the same route and
 * the difference is the whole point of the endpoint.
 */
export function savePatternOverride(
  patternId: string,
  date: string,
  times: { startMinute: number | null; endMinute: number | null },
): Promise<Pattern> {
  return write('PUT', `${patternUrl(patternId)}/overrides/${encodeURIComponent(date)}`, times)
}

/** The date goes back to the weekly rules. Not the same as blocking it. */
export function removePatternOverride(patternId: string, date: string): Promise<Pattern> {
  return write('DELETE', `${patternUrl(patternId)}/overrides/${encodeURIComponent(date)}`)
}
