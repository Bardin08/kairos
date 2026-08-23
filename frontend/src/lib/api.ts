// The HTTP surface of every page the entry chunk carries — the booking page,
// the manage page, the invitee's proposal page and integrations. None
// reimplements a fetch, because the two failure shapes below are the part that
// is easy to get wrong twice.
//
// The workbench's own calls are NOT here. They live in the admin chunk —
// routes/admin/patterns.ts, adminLinks.ts, adminSettings.ts, adminMeetings.ts —
// because a module in this file's chunk is downloaded by every invitee who
// opens a link on their phone, whether or not they could ever call it.
//
// Integrations is the one operator page whose calls stay here, and the reason is
// its route rather than its reader: /integrations is where the OAuth callback
// redirects a browser, App.tsx imports that page eagerly so the redirect never
// lands on a chunk boundary, and a call reached from an eager page is in the
// eager graph wherever it is written. Moving these two would move the bytes and
// not remove them.

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
 * `fetch` — which is the whole reason this module exists, and why the workbench's
 * own calls can live in the admin chunk without a second copy of the two failure
 * shapes above. See this file's header for which calls are where and why.
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
 * What the invitee is being asked, and what they would be leaving.
 *
 * Every instant here is the ORGANIZER's calendar, which is why `timeZone`
 * travels with them: a column of times belonging to nobody in particular is how
 * a call gets missed.
 */
export type Proposal = {
  bookingId: string
  /** ISO UTC instants — where the meeting is now, and where it is offered. */
  currentStart: string
  currentEnd: string
  proposedStart: string
  proposedEnd: string
  linkName: string
  /** When the suggestion lapses and the meeting stays put. */
  expiresAt: string
  /** The ORGANIZER's zone, the one every time on the page is drawn in. */
  timeZone: string
  /**
   * `open`, `accepted`, `declined`, `expired` or `withdrawn`.
   *
   * The server folds the clock into this: a hold whose expiry has passed reads
   * `expired` whether or not anything has been round to mark it. Nothing on the
   * client re-derives that — the expiry is the organizer's clock, not the
   * browser's, and a second opinion about it would draw Accept over a
   * suggestion that will refuse it.
   */
  state: string
}

/** The suggestion behind an accept/decline link. 404 for a token matching nothing. */
export function getProposal(token: string, signal?: AbortSignal): Promise<Proposal> {
  return request<Proposal>(`/bookings/proposals/${encodeURIComponent(token)}`, { signal })
}

/**
 * Moves the meeting, and answers the booking as it now stands.
 *
 * 409 if somebody already answered or if the hold lapsed — the two are separate
 * codes on the wire and neither is worth a separate sentence to a reader, since
 * what to do about both is to look at where things now stand.
 */
export function acceptProposal(token: string, signal?: AbortSignal): Promise<Booking> {
  return post<Booking>(`/bookings/proposals/${encodeURIComponent(token)}/accept`, undefined, signal)
}

/** Leaves the meeting exactly where it is, and answers the closed proposal. */
export function declineProposal(token: string, signal?: AbortSignal): Promise<Proposal> {
  return post<Proposal>(`/bookings/proposals/${encodeURIComponent(token)}/decline`, undefined, signal)
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
 * The stored credential will not decrypt: it was written under a different
 * `Security:SecretKey` than the deployment is running with now, so the row is
 * there and unusable.
 *
 * Arrives on `fault` rather than as a failed request, and that is the whole
 * point of it being a code on a row: reconnecting is the fix, the Connect
 * button is what does it, and a listing that refused would have taken the
 * button away with it.
 */
export const CREDENTIAL_UNREADABLE = 'credential_unreadable'

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
  /**
   * A code for a stored credential this deployment cannot use, or null. Today
   * the only one is `CREDENTIAL_UNREADABLE`. It is a separate field from
   * `reconnectRequired` because the two have different causes and want
   * different sentences — one is the provider refusing a grant, the other is
   * this deployment being unable to read what it stored — even though both end
   * at the same button.
   */
  fault: string | null
}

/** Every provider this build can connect, with its state. Never a fixed list. */
export function listIntegrations(signal?: AbortSignal): Promise<Integration[]> {
  return request<Integration[]>('/admin/integrations', { signal })
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
    `/admin/integrations/${encodeURIComponent(provider)}/connect`,
    undefined,
    signal,
  )

  return consentUrl
}
