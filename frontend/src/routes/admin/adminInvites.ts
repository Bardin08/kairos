// The personal-invites half of the operator's HTTP surface.
//
// Here rather than in lib/api.ts, for the reason adminLinks.ts states beside its
// own copy of this decision: lib/api.ts is in the booking route's entry chunk,
// so anything added to it is downloaded by every invitee on their phone. This is
// reached only through the lazily imported admin shell, and still goes through
// `adminRequest`.
//
// `AdminInvite` rather than `Invite`, the way `adminLinks.ts` names its type
// `AdminLink` rather than `Link`: lib/api.ts already exports an `Invite`, and it
// is a different shape — the invitee's view, keyed by slug and carrying no id.

import { adminRequest, adminRequestNothing } from './session'

const JSON_HEADERS = { 'Content-Type': 'application/json' }

/**
 * Where an invite stands. Derived from the row and the clock on every read and
 * never stored, so nothing here re-derives it: an expiry is the organizer's
 * clock, and a second opinion would draw Revoke over an invite that is gone.
 */
export type AdminInviteState = 'open' | 'consumed' | 'revoked' | 'expired'

/**
 * One invite as the operator's list shows it. There is no URL on this type
 * because there is none to give back: only the SHA-256 of the token is stored.
 */
export type AdminInvite = {
  id: string
  /** The link this invite is on. The row's own actions are routed per link. */
  linkId: string
  /** Its name, not its slug — a column headed Link is read by whoever named it. */
  linkName: string
  state: AdminInviteState
  /** Keyed by the booking payload's own field names — `name`, `email`, `notes`. */
  prefill: Record<string, string>
  /** ISO instant, or null when the invite never lapses. */
  expiresAt: string | null
  createdAt: string
}

/**
 * The whole body of the 201, and the one moment an invite's URL exists. A URL
 * not carried out of this response is one nobody can ever read back — the only
 * recovery is to regenerate, which mints a different invite.
 */
export type IssuedInvite = { id: string; url: string }

/**
 * What an invite is issued with. Both halves may be empty: a bag with nothing in
 * it and no deadline is a valid invite — a single-use URL that seeds nothing.
 */
export type InviteDraft = {
  prefill: Record<string, string>
  /** An ISO instant, never a duration. A preset is resolved before it is sent. */
  expiresAt: string | null
}

function invitesOf(linkId: string): string {
  return `/admin/links/${encodeURIComponent(linkId)}/invites`
}

function inviteOf(linkId: string, inviteId: string): string {
  return `${invitesOf(linkId)}/${encodeURIComponent(inviteId)}`
}

/** Every invite on every link, newest first. */
export function listAllInvites(signal?: AbortSignal): Promise<AdminInvite[]> {
  return adminRequest<AdminInvite[]>('/admin/invites', { signal })
}

export function createInvite(linkId: string, draft: InviteDraft): Promise<IssuedInvite> {
  return adminRequest<IssuedInvite>(invitesOf(linkId), {
    method: 'POST',
    headers: JSON_HEADERS,
    body: JSON.stringify(draft),
  })
}

/**
 * 204 with no body, which is why this is `adminRequestNothing`: `adminRequest`
 * would try to parse the empty body and throw a SyntaxError `asApiError` cannot
 * tell apart from a proxy returning HTML. Idempotent — a second press writes
 * nothing over an invite somebody already spent.
 */
export function revokeInvite(linkId: string, inviteId: string): Promise<void> {
  return adminRequestNothing(`${inviteOf(linkId, inviteId)}/revoke`, { method: 'POST' })
}

/**
 * A replacement for a URL that was lost, as a new invite with a new id. Refused
 * 409 `invite_not_open` on any other state, which is why the button offering it
 * is drawn on an open invite only.
 */
export function regenerateInvite(linkId: string, inviteId: string): Promise<IssuedInvite> {
  return adminRequest<IssuedInvite>(`${inviteOf(linkId, inviteId)}/regenerate`, {
    method: 'POST',
  })
}
