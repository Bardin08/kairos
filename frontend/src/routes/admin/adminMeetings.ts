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
// of its own: `request` is the one place a failure becomes an ApiError, and a
// second copy of that is the copy that gets it wrong.
//
// Named adminMeetings rather than meetings, like adminLinks.ts and
// adminSettings.ts beside it: Meetings.tsx sits in this directory, and on a
// case-insensitive filesystem `./meetings` and `./Meetings` are the same name
// to the module resolver.

import { request } from '../../lib/api'

/** One link, as the meetings filter offers it. Only links that have meetings. */
export type MeetingLink = {
  id: string
  name: string
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
  participantName: string
  participantEmail: string
  /** The PARTICIPANT's zone, as they gave it. Never the organizer's. */
  participantTimeZone: string
  calendarSynced: boolean
  /** Lowercase provider name, or null when nothing was ever written. */
  calendarProvider: string | null
  calendarError: string | null
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
  return request<Meetings>(`/admin/meetings${search === '' ? '' : `?${search}`}`, { signal })
}
