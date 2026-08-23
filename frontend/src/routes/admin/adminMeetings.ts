// The meetings half of the operator's HTTP surface.
//
// Here rather than in lib/api.ts, and the reason is measured rather than
// stylistic: lib/api.ts is imported by the booking page, so anything left in it
// lands in the entry chunk and is downloaded by every invitee who opens a link
// on their phone. `listMeetings` was in there until Part C weighed it — the
// string `admin/meetings` was in index-*.js, on a route that can never call it
// — while the page that uses it was correctly absent. This module is imported
// only by Meetings.tsx, which is reachable only through the lazily imported
// admin shell, so Rollup places it in the admin chunk. It still makes no fetch
// of its own: it goes through `adminRequest`, which wraps lib/api's `request` —
// still the one place a failure becomes an ApiError — and latches session.ts on
// a 401 so the shell can replace itself with the locked screen.
//
// Named adminMeetings rather than meetings, like adminLinks.ts and
// adminSettings.ts beside it: Meetings.tsx sits in this directory, and on a
// case-insensitive filesystem `./meetings` and `./Meetings` are the same name
// to the module resolver.

import { adminRequest } from './session'

const JSON_HEADERS = { 'Content-Type': 'application/json' }

/** One link, as the meetings filter offers it. Only links that have meetings. */
export type MeetingLink = {
  id: string
  name: string
}

/**
 * The newest hold on a meeting, whatever became of it.
 *
 * One per row and never a list: an open hold is a Withdraw button, and a closed
 * one is at most a sentence saying what happened. An operator who proposed
 * again after a decline is asking about the new suggestion.
 */
export type MeetingProposal = {
  id: string
  /** ISO UTC instant of the suggested time. Render it in the organizer's zone. */
  start: string
  /** When it lapses and the meeting stays put. */
  expiresAt: string
  /**
   * `open`, `declined`, `expired` or `withdrawn` — `accepted` does not appear
   * on a row that has not also moved, since accepting is what moves it.
   *
   * The server folds the clock in: a hold whose expiry has passed arrives as
   * `expired` whether or not the sweeper has been round to mark it. Nothing
   * here re-derives that, because the expiry is the organizer's clock and not
   * the browser's.
   */
  state: string
}

/**
 * One meeting as the operator's list draws it.
 *
 * `calendarSynced` is the whole of the sync question: false covers a booking
 * nobody connected a calendar for, a write that failed, and a write nothing
 * finished. `calendarError` is the provider's own words when there are any, and
 * it is why "not synced" does not send the operator to the server log.
 */
export type Meeting = {
  id: string
  /** ISO UTC instant, `2026-09-07T09:00:00Z`. Render it in `timeZone`. */
  start: string
  end: string
  durationMinutes: number
  status: string
  linkId: string
  /** The link this was booked through — the one fact a calendar cannot supply. */
  linkName: string
  /**
   * That link's slug, beside its id and not instead of it. The propose panel
   * reads what it may offer from `GET /links/{slug}/slots`, which takes no link
   * id at all.
   */
  slug: string
  participantName: string
  participantEmail: string
  /** The PARTICIPANT's zone, as they gave it. Never the organizer's. */
  participantTimeZone: string
  calendarSynced: boolean
  /** Lowercase provider name, or null when nothing was ever written. */
  calendarProvider: string | null
  calendarError: string | null
  /** Null when nobody has ever suggested a new time for this meeting. */
  proposal: MeetingProposal | null
}

/**
 * One render of the meetings tab.
 *
 * `links` is the filter's options and does NOT narrow with the filters — a
 * dropdown that collapsed to the link just chosen would be a filter with no way
 * back out of itself.
 */
export type Meetings = {
  /** The ORGANIZER's zone, from Settings. Every time on the page renders in it. */
  timeZone: string
  links: MeetingLink[]
  meetings: Meeting[]
}

/**
 * Every meeting, newest last, narrowed by whatever is given.
 *
 * `from` and `to` are ISO calendar dates in the ORGANIZER's zone and are
 * inclusive at both ends. Every filter is optional; sending none asks for the
 * lot. Answers 401 when the operator's session has expired, which is a sign-in
 * and not an error to render.
 */
export function listMeetings(
  filter: { linkId?: string; from?: string; to?: string },
  signal?: AbortSignal,
): Promise<Meetings> {
  const query = new URLSearchParams()
  if (filter.linkId !== undefined) query.set('linkId', filter.linkId)
  if (filter.from !== undefined) query.set('from', filter.from)
  if (filter.to !== undefined) query.set('to', filter.to)

  const search = query.toString()
  return adminRequest<Meetings>(`/admin/meetings${search === '' ? '' : `?${search}`}`, { signal })
}

/**
 * Calls a meeting off, with a reason the invitee reads in their cancellation.
 *
 * The slot and its margin shadow come back on every link, and the invitee's
 * calendar drops the event. Answers 404 for a meeting that is not there and 200
 * for one already cancelled — pressing it twice is not an error.
 */
export function cancelMeeting(id: string, reason: string, signal?: AbortSignal): Promise<Meeting> {
  return adminRequest<Meeting>(`/admin/meetings/${id}/cancel`, {
    method: 'POST',
    headers: JSON_HEADERS,
    body: JSON.stringify({ reason }),
    signal,
  })
}

/**
 * Offers the invitee a new time and holds it until they answer. Nothing about
 * the meeting changes here: the invitee agreed to a time, so the operator
 * proposes and the invitee decides.
 *
 * `start` is one of the strings `getSlots` returned, verbatim — rebuilt as an
 * offset form it is refused as `instant_invalid`. Answers 409 for a meeting
 * that already has an open hold (`proposal_open`), for a deployment with no
 * mail relay (`mail_unconfigured`), and for a relay that refused the send
 * (`proposal_undeliverable`); nothing is held in any of the three.
 */
export function proposeMeeting(id: string, start: string, signal?: AbortSignal): Promise<Meeting> {
  return adminRequest<Meeting>(`/admin/meetings/${id}/proposals`, {
    method: 'POST',
    headers: JSON_HEADERS,
    body: JSON.stringify({ start }),
    signal,
  })
}

/**
 * Takes a suggestion back and gives its slot up.
 *
 * Without it an operator who named the wrong time holds a slot hostage for the
 * whole window and cannot offer the right one, because only one hold may be
 * open at a time. Idempotent: withdrawing something already answered answers
 * with the row as it stands rather than a 409.
 */
export function withdrawProposal(
  id: string,
  proposalId: string,
  signal?: AbortSignal,
): Promise<Meeting> {
  return adminRequest<Meeting>(`/admin/meetings/${id}/proposals/${proposalId}`, {
    method: 'DELETE',
    signal,
  })
}
