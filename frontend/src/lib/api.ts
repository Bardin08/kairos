// The whole HTTP surface, in one place, for every page. The booking page uses
// the first three; the manage page uses the three after those; the integrations
// page uses the last two. None reimplements a fetch, because the two failure
// shapes below are the part that is easy to get wrong twice.

const BASE = '/api/v1'
const JSON_HEADERS = { 'Content-Type': 'application/json' }

/**
 * A write waited out the full SQLite busy timeout and gave up. 503, and the
 * write did NOT happen, so it is safe to send again — which is what separates
 * it from a 409. A 409 means the slot is gone and the user must choose
 * differently; this means nothing at all changed and the same request will
 * probably work. Every write can answer it: create, cancel and reschedule.
 */
export const DATABASE_BUSY = 'database_busy'

/**
 * The app cancelled this request — a duration changed, a month turned, a
 * component unmounted. Not a failure, and never something to render. It exists
 * so a caller can tell "we called this off" apart from "the network is gone",
 * which otherwise look identical from a catch block.
 */
export const REQUEST_ABORTED = 'request_aborted'

/** One bookable event type, as the booking page needs it. */
export type Link = {
  slug: string
  name: string
  description: string
  /** The link's own order, not ascending. Render the tabs in it. */
  durations: number[]
  /** The ORGANIZER's zone. Every instant the API returns is derived from it. */
  timeZone: string
}

/** One booking, as every booking endpoint answers it. */
export type Booking = {
  id: string
  slug: string
  /** ISO UTC instant, `2026-09-07T09:00:00Z`. */
  start: string
  end: string
  durationMinutes: number
  status: string
  sequence: number
  name: string
  email: string
  /** The BOOKER's zone, as they gave it. */
  timeZone: string
  notes: string
  /**
   * Non-null exactly once, in the 201 from `createBooking`. The database keeps
   * only a SHA-256 hash of it, so nothing can hand it back a second time — a
   * booking whose token is not carried into a URL at this moment is a booking
   * nobody can ever cancel or move.
   */
  manageToken: string | null
  /**
   * Where this meeting is in the organizer's calendar: `synced`, `failed`,
   * `pending` or `cancelled`. Null means nothing was ever attempted — the
   * ordinary state of a deployment with no calendar connected — so it must not
   * be rendered the same as `failed`, or every booking on a Kairos that was
   * never asked to sync carries a red mark.
   *
   * The provider's own error text is deliberately not exposed: it is unfiltered
   * third-party prose on a response a manage token can read, and it routinely
   * carries the organizer's account address.
   */
  calendarSync: string | null
  /** Where to join, or null if this meeting has no second channel. */
  conferenceUrl: string | null
}

/**
 * A failed request, with the machine-readable code when the server sent one.
 *
 * Two 400 shapes come back from this API. A domain failure carries `code`
 * (`slot_taken`, `pattern_invalid`, …). A DataAnnotations failure carries
 * `errors` and no `code` at all, and is nothing more specific than a bad
 * request — so `code` is null there, and callers branching on a code must
 * treat null as generic rather than as a code they have not seen yet.
 */
export class ApiError extends Error {
  readonly status: number
  readonly code: string | null
  /**
   * Seconds from the `Retry-After` header, when the server sent one. It is the
   * server's own busy timeout rather than a number this client invented, so a
   * wait built on it stays correct when that timeout is retuned.
   */
  readonly retryAfter: number | null

  constructor(
    status: number,
    code: string | null,
    message: string,
    retryAfter: number | null = null,
  ) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.retryAfter = retryAfter
  }
}

/**
 * Whatever a rejected call threw, as something with a status and a code.
 *
 * A `catch` here receives five different things: an ApiError this module built;
 * an AbortError when the app itself cancelled the request; a TypeError from
 * `fetch` when the network is not there; a SyntaxError if a 200 arrives with a
 * body that is not JSON; and a RangeError if `Intl` was handed a zone that does
 * not exist. The last four carry messages written for a developer console, so
 * they are replaced rather than shown to an invitee.
 *
 * Status 0 means no HTTP status was reached — the request was cancelled, or it
 * never got an answer. It does NOT mean the request failed to complete: a 200
 * whose body is not JSON lands here too, having very much got an answer.
 *
 * An abort gets its own code because it is not a failure at all. Both callers
 * check `signal.aborted` before they touch the result, so nothing leaks today,
 * but this module is shared and the first caller to forget that guard would
 * otherwise tell an invitee "could not reach the server" about a request the
 * app cancelled on purpose.
 */
export function asApiError(error: unknown): ApiError {
  if (error instanceof ApiError) return error
  // Matched on the name, not on `instanceof DOMException`, which is
  // realm-sensitive: under the jsdom test environment the abort arrives as a
  // DOMException built in another realm and the instanceof is false, so the
  // guard was correct in a browser and untestable here. The name is the same
  // in every realm.
  if (error instanceof Error && error.name === 'AbortError') {
    return new ApiError(0, REQUEST_ABORTED, 'The request was cancelled.')
  }
  // `Intl` is the only thing in this app that throws RangeError, and it throws
  // it for exactly one reason: a zone identifier it does not recognise. That
  // can only be the organizer's, since the invitee's comes from the browser.
  // Without this the page blamed the network for a misconfigured link.
  if (error instanceof RangeError) {
    return new ApiError(
      0,
      null,
      'This link is set to a time zone that cannot be read, so its times cannot be worked out. Whoever owns the link has to fix it.',
    )
  }
  return new ApiError(0, null, 'Could not reach the server. Try again in a moment.')
}

async function toError(response: Response): Promise<ApiError> {
  let title = ''
  let code: string | null = null

  try {
    const body: unknown = await response.json()
    if (body !== null && typeof body === 'object') {
      const problem = body as { title?: unknown; code?: unknown }
      if (typeof problem.title === 'string') title = problem.title
      if (typeof problem.code === 'string') code = problem.code
    }
  } catch {
    // A proxy error page, or an empty body. The status is all there is.
  }

  // Delta-seconds, which is the only form this API sends. Matched as digits
  // before it is converted, because `Number` also accepts an HTTP-date's
  // neighbours — "12.5", "0x10", "1e3" — and the last of those would put "try
  // again in 1000 seconds" in front of an invitee.
  const header = response.headers.get('Retry-After') ?? ''
  const seconds = /^\d+$/.test(header) ? Number(header) : 0

  return new ApiError(
    response.status,
    code,
    title || `The server answered ${response.status}.`,
    seconds > 0 ? seconds : null,
  )
}

/**
 * One request, and the only place a failure becomes an ApiError.
 *
 * Exported so a module outside this file can make a call without reimplementing
 * `fetch` — which is the whole reason this module exists. The admin surface's
 * own calls live in the admin chunk rather than here: they are useless to an
 * invitee, and code reachable from the booking route is code every invitee
 * downloads on mobile data whether they can use it or not.
 */
export async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(BASE + path, init)
  if (!response.ok) throw await toError(response)
  return (await response.json()) as T
}

/**
 * The same, for a response with no body to parse — a 204. `request` would
 * choke on the empty body with a SyntaxError that `asApiError` cannot tell
 * apart from a proxy returning HTML.
 */
export async function requestNothing(path: string, init?: RequestInit): Promise<void> {
  const response = await fetch(BASE + path, init)
  if (!response.ok) throw await toError(response)
}

function post<T>(path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
  return request<T>(path, {
    method: 'POST',
    headers: JSON_HEADERS,
    body: JSON.stringify(body ?? {}),
    signal,
  })
}

export function getLink(slug: string, signal?: AbortSignal): Promise<Link> {
  return request<Link>(`/links/${encodeURIComponent(slug)}`, { signal })
}

/**
 * Every start this link offers between two dates, inclusive, as the verbatim
 * strings a booking has to echo back. `from` and `to` are ISO calendar dates in
 * the ORGANIZER's zone and may span at most 31 days. `duration` may be omitted
 * only when the link offers exactly one.
 */
export function getSlots(
  slug: string,
  range: { from: string; to: string; duration?: number },
  signal?: AbortSignal,
): Promise<string[]> {
  const query = new URLSearchParams({ from: range.from, to: range.to })
  if (range.duration !== undefined) query.set('duration', String(range.duration))

  return request<string[]>(
    `/links/${encodeURIComponent(slug)}/slots?${query}`,
    { signal },
  )
}

/**
 * 201 on success, and the only response that ever carries `manageToken`.
 * `durationMinutes` is required even for a single-duration link.
 */
export function createBooking(
  booking: {
    slug: string
    start: string
    durationMinutes: number
    name: string
    email: string
    /** The BOOKER's IANA zone — the one they read the times in. */
    timeZone: string
    notes: string
  },
  signal?: AbortSignal,
): Promise<Booking> {
  return post<Booking>('/bookings', booking, signal)
}

export function getBooking(token: string, signal?: AbortSignal): Promise<Booking> {
  return request<Booking>(`/bookings/${encodeURIComponent(token)}`, { signal })
}

/** Idempotent: cancelling an already-cancelled booking answers 200, not 404. */
export function cancelBooking(
  token: string,
  signal?: AbortSignal,
): Promise<Booking> {
  return post<Booking>(
    `/bookings/${encodeURIComponent(token)}/cancel`,
    undefined,
    signal,
  )
}

/** The length does not change; only where the meeting sits. */
export function rescheduleBooking(
  token: string,
  start: string,
  signal?: AbortSignal,
): Promise<Booking> {
  return post<Booking>(
    `/bookings/${encodeURIComponent(token)}/reschedule`,
    { start },
    signal,
  )
}

/**
 * The deployment has no client credentials for that provider, so the consent
 * screen it would send the operator to does not exist. Answered by `connect`,
 * and predicted by `configured: false` on the row — the page reads the flag and
 * does not offer the button, so this code arrives only when the configuration
 * changed under a page that was already open.
 */
export const PROVIDER_NOT_CONFIGURED = 'provider_not_configured'

/**
 * One provider and where its connection stands.
 *
 * `provider` is the lowercase enum name — the same string the connect path
 * takes — and nothing here is specific to any one vendor. The integrations page
 * renders whatever this array holds, so a provider the backend learns to
 * connect appears in the UI without a line of frontend changing.
 *
 * The four states the page distinguishes are all in these flags, and they are
 * not the same question asked twice. `configured` is about the deployment: no
 * client credentials, nothing to connect to. `connected` is about the operator:
 * a credential is stored. `reconnectRequired` is about that credential still
 * working — a grant revoked in the provider's own account settings leaves the
 * row connected and useless, and only a fresh consent fixes it.
 */
export type Integration = {
  provider: string
  configured: boolean
  connected: boolean
  /** The connected account, as the provider names it. An email address today. */
  accountLabel: string | null
  /** Extended-ISO instant, `2026-08-14T22:29:14Z`, or null when not connected. */
  connectedAt: string | null
  reconnectRequired: boolean
}

/** Every provider this build can connect, with its state. Never a fixed list. */
export function listIntegrations(signal?: AbortSignal): Promise<Integration[]> {
  return request<Integration[]>('/integrations', { signal })
}

/**
 * Where to send the browser to ask the provider for consent.
 *
 * The URL is returned rather than followed, and the caller must NAVIGATE to it
 * — `location.assign`, not `fetch`. It is another origin's consent screen: a
 * fetch would be refused by CORS, and even if it were not, the thing on the far
 * end is a page for a human to press a button on, not an API.
 */
export async function connectIntegration(
  provider: string,
  signal?: AbortSignal,
): Promise<string> {
  const { consentUrl } = await post<{ consentUrl: string }>(
    `/integrations/${encodeURIComponent(provider)}/connect`,
    undefined,
    signal,
  )

  return consentUrl
}

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
