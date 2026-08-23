// The availability half of the operator's HTTP surface.
//
// Here rather than in lib/api.ts, and the reason is measured: lib/api.ts is
// imported by the booking page, so anything added to it lands in the entry chunk
// — 240 gzipped bytes of endpoints the invitee can never call. This module is
// imported only by Availability.tsx, reachable only through the lazily imported
// admin shell, so Rollup places it in the admin chunk. It still makes no fetch
// of its own: `adminRequest` wraps lib/api's `request`, still the one place a
// failure becomes an ApiError, and latches session.ts on a 401.

import { adminRequest, adminRequestNothing } from './session'

/** One weekly window. Minutes from local midnight, in the ORGANIZER's zone. */
export type PatternRule = {
  id: string
  /** ISO-8601: 1 is Monday, 7 is Sunday. Not `Date.getDay()`, whose Sunday is 0. */
  isoDayOfWeek: number
  startMinute: number
  endMinute: number
}

/** One window on a date. Minutes from local midnight, in the ORGANIZER's zone. */
export type OverrideWindow = {
  startMinute: number
  endMinute: number
}

/**
 * What one date does instead of the weekly rules.
 *
 * No windows is not missing data — it is the day being blocked entirely, which
 * is how a holiday is expressed. A UI that reads the empty list as "not loaded
 * yet" turns a deliberate closure into a rendering accident. A date the pattern
 * says nothing about is absent from `Pattern.overrides` altogether.
 */
export type PatternOverride = {
  /** A date key, `2026-12-25`. No time, so no zone of its own. */
  date: string
  /** Earliest first, as the server sends them. Empty blocks the date. */
  windows: OverrideWindow[]
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
  return adminRequest<Pattern>(path, {
    method,
    headers: body === undefined ? undefined : JSON_HEADERS,
    body: body === undefined ? undefined : JSON.stringify(body),
  })
}

export function listPatterns(signal?: AbortSignal): Promise<Pattern[]> {
  return adminRequest<Pattern[]>(PATTERNS, { signal })
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
  return adminRequestNothing(patternUrl(patternId), { method: 'DELETE' })
}

/** The whole weekly shape in one request: the server refuses or stores the set entire. */
export function replacePatternRules(
  patternId: string,
  rules: readonly { isoDayOfWeek: number; startMinute: number; endMinute: number }[],
): Promise<Pattern> {
  return write('PUT', `${patternUrl(patternId)}/rules`, { rules })
}

/**
 * The date's whole set in one request, the way the week's is: the server stores
 * it entire or refuses it entire, so a date holding a morning and an afternoon
 * is one write and not two.
 *
 * Keyed on the date, so it is a replacement and never a create-or-update the
 * caller has to choose between. An empty list blocks the date; DELETE on the
 * same route puts it back on the weekly rules.
 */
export function savePatternOverride(
  patternId: string,
  date: string,
  windows: readonly OverrideWindow[],
): Promise<Pattern> {
  return write('PUT', `${patternUrl(patternId)}/overrides/${encodeURIComponent(date)}`, {
    windows,
  })
}

/** The date goes back to the weekly rules. Not the same as blocking it. */
export function removePatternOverride(patternId: string, date: string): Promise<Pattern> {
  return write('DELETE', `${patternUrl(patternId)}/overrides/${encodeURIComponent(date)}`)
}
