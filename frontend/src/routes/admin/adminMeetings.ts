// The meetings half of the operator's HTTP surface.
//
// Here rather than in lib/api.ts, and the reason is measured: `listMeetings` was
// in there until Part C weighed it — the string `admin/meetings` was in
// index-*.js, on a route that can never call it. This is reached only through
// the lazily imported admin shell, and still goes through `adminRequest`.
//
// Named adminMeetings rather than meetings: Meetings.tsx sits in this directory,
// and on a case-insensitive filesystem the two are the same name.

import { adminRequest } from './session'

const JSON_HEADERS = { 'Content-Type': 'application/json' }

/** One link, as the meetings filter offers it. Only links that have meetings. */
export type MeetingLink = {
  id: string
  name: string
}

/**
 * The newest hold on a meeting, whatever became of it. One per row and never a
 * list: an open hold is a Withdraw button, a closed one at most a sentence. An
 * operator who proposed again after a decline is asking about the new one.
 */
export type MeetingProposal = {
  id: string
  /** ISO UTC instant of the suggested time. Render it in the organizer's zone. */
  start: string
  /** When it lapses and the meeting stays put. */
  expiresAt: string
  /**
   * `open`, `declined`, `expired` or `withdrawn` — `accepted` does not appear on
   * a row that has not also moved, since accepting is what moves it. The server
   * folds the clock in: a hold whose expiry has passed arrives as `expired`
   * whether or not the sweeper has marked it, and nothing here re-derives that,
   * because the expiry is the organizer's clock and not the browser's.
   */
  state: string
}

/**
 * One meeting as the operator's list draws it. `calendarSynced` false covers a
 * booking nobody connected a calendar for, a write that failed, and a write
 * nothing finished; `calendarError` carries the provider's own words, which is
 * why "not synced" does not send the operator to the server log.
 */
/**
 * The four facts the cancel dialog restates about the meeting it is destroying.
 *
 * A subset of `Meeting` rather than the whole row, and that is the point: the
 * dialog needs to name what is about to go, not to know everything about it.
 * `--surface-overlay` is drawn with `blur(8px)`, so the row underneath the
 * dialog is unreadable — a dialog that leaned on it for context would be
 * leaning on something the backdrop erased.
 */
export type CancelSubject = Pick<Meeting, 'participantName' | 'linkName' | 'start' | 'end'>

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
   * That link's slug, beside its id and not instead of it: the propose panel
   * reads what it may offer from `GET /links/{slug}/slots`, which takes no id.
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
 * One render of the meetings tab. `links` is the filter's options and does NOT
 * narrow with the filters — a dropdown that collapsed to the link just chosen
 * would be a filter with no way back out of itself.
 */
export type Meetings = {
  /** The ORGANIZER's zone, from Settings. Every time on the page renders in it. */
  timeZone: string
  links: MeetingLink[]
  meetings: Meeting[]
}

/**
 * Every meeting, newest last, narrowed by whatever is given. `from` and `to` are
 * ISO calendar dates in the ORGANIZER's zone, inclusive at both ends; every
 * filter is optional. Answers 401 when the session has expired, which is a
 * sign-in and not an error to render.
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
 * The slot and its margin shadow come back on every link. Answers 404 for a
 * meeting that is not there and 200 for one already cancelled — pressing it
 * twice is not an error.
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
 * the meeting changes: the invitee agreed to a time, so the operator proposes
 * and the invitee decides.
 *
 * `start` is one of the strings `getSlots` returned, verbatim — rebuilt as an
 * offset form it is refused as `instant_invalid`. Answers 409 for a meeting that
 * already has an open hold (`proposal_open`), a deployment with no mail relay
 * (`mail_unconfigured`), and a relay that refused the send
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
 * Takes a suggestion back and gives its slot up. Without it an operator who
 * named the wrong time holds a slot hostage for the whole window and cannot
 * offer the right one, because only one hold may be open at a time. Idempotent:
 * withdrawing something already answered answers with the row as it stands.
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
