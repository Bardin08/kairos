// The links half of the operator's HTTP surface.
//
// Here rather than in lib/api.ts, and the reason is measured: lib/api.ts is in
// the booking route's entry chunk, so anything added to it is downloaded as
// endpoints the invitee can never call. This is reached only through the lazily
// imported admin shell, and still goes through `adminRequest`.
//
// Named adminLinks rather than links: Links.tsx sits in this directory, and on a
// case-insensitive filesystem `./links` and `./Links` are the same name.

import { adminRequest } from './session'

const LINKS = '/admin/links'
const JSON_HEADERS = { 'Content-Type': 'application/json' }

/**
 * One link as the workbench edits it.
 *
 * The four limits are the database's own values and are NOT resolved: `null`
 * inherits the global, a number does not, and `0` is a number. Anything that
 * flattens the two — a `?? globals.margin` in, a `|| null` out — is invisible
 * afterwards: a link showing 15 looks the same either way.
 */
export type AdminLink = {
  id: string
  slug: string
  name: string
  description: string
  patternId: string
  patternName: string
  active: boolean
  /** The link's own order, not ascending. It is the order of the booking page's tabs. */
  durations: number[]
  marginMinutes: number | null
  minNoticeMinutes: number | null
  dailyCap: number | null
  startIncrementMinutes: number | null
}

/** A pattern as a thing to pick. A link row is not renderable from an id alone. */
export type AdminPattern = { id: string; name: string }

/** The globals, resolved and never null — what an inherited limit is inheriting. */
export type GlobalLimits = {
  marginMinutes: number
  minNoticeMinutes: number
  dailyCap: number
  startIncrementMinutes: number
}

export type LinksPage = {
  links: AdminLink[]
  patterns: AdminPattern[]
  globals: GlobalLimits
}

/**
 * The four, exactly as the PUT takes them: all of them, every time. A full
 * replace and not a patch — under patch semantics "absent" and "explicitly null"
 * are different intentions, and that is the distinction that must survive here.
 */
export type LinkLimits = Pick<
  AdminLink,
  'marginMinutes' | 'minNoticeMinutes' | 'dailyCap' | 'startIncrementMinutes'
>

/** A link's editable shape. No limits and no `active`: both have their own routes. */
export type LinkDraft = {
  slug: string
  name: string
  description: string
  patternId: string
  durations: number[]
}

function send<T>(path: string, method: string, body?: unknown): Promise<T> {
  return adminRequest<T>(path, {
    method,
    headers: JSON_HEADERS,
    body: JSON.stringify(body ?? {}),
  })
}

/** Every link, archived ones included — this is where one is brought back from. */
export function listLinks(signal?: AbortSignal): Promise<LinksPage> {
  return adminRequest<LinksPage>(LINKS, { signal })
}

export function createLink(draft: LinkDraft): Promise<AdminLink> {
  return send<AdminLink>(LINKS, 'POST', draft)
}

export function updateLink(id: string, draft: LinkDraft): Promise<AdminLink> {
  return send<AdminLink>(`${LINKS}/${encodeURIComponent(id)}`, 'PUT', draft)
}

export function updateLinkLimits(id: string, limits: LinkLimits): Promise<AdminLink> {
  return send<AdminLink>(`${LINKS}/${encodeURIComponent(id)}/limits`, 'PUT', limits)
}

/** Off, not gone: the public page 404s on it, the bookings already made stay resolvable. */
export function archiveLink(id: string): Promise<AdminLink> {
  return send<AdminLink>(`${LINKS}/${encodeURIComponent(id)}/archive`, 'POST')
}

export function restoreLink(id: string): Promise<AdminLink> {
  return send<AdminLink>(`${LINKS}/${encodeURIComponent(id)}/restore`, 'POST')
}
