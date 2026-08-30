// The HTTP surface of every page the entry chunk carries — the booking page,
// the manage page, the invitee's proposal page and integrations. None
// reimplements a fetch, because the two failure shapes below are the part that
// is easy to get wrong twice.
//
// The workbench's own calls live in the admin chunk instead — patterns.ts,
// adminLinks.ts, adminSettings.ts, adminMeetings.ts — because a module in this
// chunk is downloaded by every invitee.
//
// Integrations is the exception because of its route: /integrations is where the
// OAuth callback redirects, App.tsx imports it eagerly so the redirect never
// lands on a chunk boundary, and a call reached from an eager page is eager
// wherever it is written. Moving them moves the bytes, not removes them.

const BASE = '/api/v1'
const JSON_HEADERS = { 'Content-Type': 'application/json' }

/**
 * A write waited out the full SQLite busy timeout and gave up. 503, and the
 * write did NOT happen, so it is safe to send again — unlike a 409, where the
 * slot is gone. Every write can answer it: create, cancel and reschedule.
 */
export const DATABASE_BUSY = 'database_busy'

/**
 * The app cancelled this request — a duration changed, a month turned, a
 * component unmounted. Not a failure. It lets a caller tell "we called this off"
 * apart from "the network is gone", which look identical from a catch block.
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
   * only a SHA-256 hash of it, so a booking whose token is not carried into a
   * URL at this moment is a booking nobody can ever cancel or move.
   */
  manageToken: string | null
  /**
   * `synced`, `failed`, `pending` or `cancelled`. Null means nothing was ever
   * attempted — the ordinary state of a deployment with no calendar connected —
   * so it must not be rendered like `failed`.
   *
   * The provider's own error text is deliberately not exposed: unfiltered
   * third-party prose on a response a manage token can read, routinely carrying
   * the organizer's account address.
   */
  calendarSync: string | null
  /** Where to join, or null if this meeting has no second channel. */
  conferenceUrl: string | null
}

/**
 * A failed request, with the machine-readable code when the server sent one.
 *
 * Two 400 shapes come back. A domain failure carries `code` (`slot_taken`,
 * `pattern_invalid`, …); a DataAnnotations failure carries `errors` and no
 * `code`, so callers branching on a code must treat null as generic.
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
 * A `catch` here receives five things: an ApiError this module built; an
 * AbortError when the app cancelled the request; a TypeError from `fetch` when
 * the network is not there; a SyntaxError if a 200 arrives with a body that is
 * not JSON; and a RangeError if `Intl` was handed a zone that does not exist.
 * The last four carry developer-console messages, so they are replaced.
 *
 * Status 0 means no HTTP status was reached, not that the request failed to
 * complete: a 200 whose body is not JSON lands here too.
 *
 * An abort gets its own code because it is not a failure. Both callers check
 * `signal.aborted`, but this module is shared and the first to forget would tell
 * an invitee "could not reach the server" about a deliberate cancel.
 */
export function asApiError(error: unknown): ApiError {
  if (error instanceof ApiError) return error
  // Matched on the name, not on `instanceof DOMException`, which is
  // realm-sensitive: under jsdom the abort arrives as a DOMException built in
  // another realm and the instanceof is false. The name is the same everywhere.
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
 * One request, and the only place a failure becomes an ApiError. Exported so a
 * module outside this file — the admin chunk — can make a call without a second
 * copy of the two failure shapes above.
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
 * the ORGANIZER's zone, at most 31 days apart. `duration` may be omitted only
 * when the link offers exactly one.
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
    /**
     * The token off a personal invite's URL, absent for an ordinary booking. The
     * invite is spent by this write, inside the transaction that takes the slot,
     * so a token is never remembered server-side between the read and this call.
     */
    inviteToken?: string
  },
  signal?: AbortSignal,
): Promise<Booking> {
  return post<Booking>('/bookings', booking, signal)
}

export function getBooking(token: string, signal?: AbortSignal): Promise<Booking> {
  return request<Booking>(`/bookings/${encodeURIComponent(token)}`, { signal })
}

/**
 * The invitation this URL carries could not be spent: nobody was issued that
 * token, or it has already been used, or the operator took it back, or its
 * deadline passed, or it belongs to another link. One code for all five, and the
 * recovery is the same for all five — book without it. A 409, but NOT a lost
 * slot: nothing was written and the time is still free.
 */
export const INVITE_SPENT = 'invite_spent'

/** Where a personal invite stands. Only `open` still fills a form in. */
export type InviteState = 'open' | 'consumed' | 'revoked' | 'expired'

/** One personal invite, as the booking page needs it before it draws a form. */
export type Invite = {
  slug: string
  state: InviteState
  /** ISO instant, or null when the invite never lapses. */
  expiresAt: string | null
  /** Keyed by the booking payload's own field names — `notes`, not `Notes`. */
  prefill: Record<string, string>
}

/**
 * 404 only for a token no invite has. Consumed, revoked and expired all answer
 * 200 carrying their state, because a stale invite is still a booking page
 * somebody should be able to use.
 */
export function getInvite(token: string, signal?: AbortSignal): Promise<Invite> {
  return request<Invite>(`/invites/${encodeURIComponent(token)}`, { signal })
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
 * What the invitee is being asked, and what they would be leaving. Every instant
 * here is the ORGANIZER's calendar, which is why `timeZone` travels with them: a
 * column of times belonging to nobody in particular is how a call gets missed.
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
   * `open`, `accepted`, `declined`, `expired` or `withdrawn`. The server folds
   * the clock into this: a hold whose expiry has passed reads `expired` whether
   * or not anything has marked it. Nothing on the client re-derives that — the
   * expiry is the organizer's clock, and a second opinion would draw Accept over
   * a suggestion that will refuse it.
   */
  state: string
}

/** The suggestion behind an accept/decline link. 404 for a token matching nothing. */
export function getProposal(token: string, signal?: AbortSignal): Promise<Proposal> {
  return request<Proposal>(`/bookings/proposals/${encodeURIComponent(token)}`, { signal })
}

/**
 * Moves the meeting, and answers the booking as it now stands. 409 if somebody
 * already answered or if the hold lapsed — separate codes on the wire, but the
 * reader does the same thing about both: look at where things now stand.
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
 * screen it would send the operator to does not exist. Answered by `connect` and
 * predicted by `configured: false` on the row, so this code arrives only when
 * the configuration changed under a page that was already open.
 */
export const PROVIDER_NOT_CONFIGURED = 'provider_not_configured'

/**
 * The stored credential will not decrypt: written under a different
 * `Security:SecretKey` than the deployment runs with now, so the row is there
 * and unusable. It arrives on `fault` rather than as a failed request because
 * reconnecting is the fix, and a listing that refused would have taken the
 * Connect button away with it.
 */
export const CREDENTIAL_UNREADABLE = 'credential_unreadable'

/**
 * One provider and where its connection stands. `provider` is the lowercase enum
 * name the connect path takes, and nothing here is vendor-specific, so a
 * provider the backend learns to connect appears without a frontend change.
 *
 * The flags are not one question asked three times. `configured` is about the
 * deployment: no client credentials, nothing to connect to. `connected` is about
 * the operator: a credential is stored. `reconnectRequired` is about that
 * credential still working — a grant revoked in the provider's own settings
 * leaves the row connected and useless, and only fresh consent fixes it.
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
   * The operator's switch. False means the connection is kept and nothing is
   * being read or written with it — which is not `connected: false`, where there
   * is no credential at all. True for a provider nobody has connected, because a
   * connection that does not exist is not one somebody paused.
   */
  enabled: boolean
  /**
   * A code for a stored credential this deployment cannot use, or null; today
   * only `CREDENTIAL_UNREADABLE`. Separate from `reconnectRequired` because the
   * causes differ — the provider refusing a grant, against this deployment being
   * unable to read what it stored — even though both end at the same button.
   */
  fault: string | null
}

/** Every provider this build can connect, with its state. Never a fixed list. */
export function listIntegrations(signal?: AbortSignal): Promise<Integration[]> {
  return request<Integration[]>('/admin/integrations', { signal })
}

/**
 * Where to send the browser to ask the provider for consent. Returned rather
 * than followed, and the caller must NAVIGATE to it — `location.assign`, not
 * `fetch`. It is another origin's consent screen: a fetch is refused by CORS,
 * and the far end is a page for a human to press a button on.
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

/**
 * One choice a field offers. `detail` is the provider's own second line —
 * "Primary", "Read-only subscription", "No longer reachable" — and the panel
 * renders it without knowing which provider wrote it.
 */
export type SettingOption = {
  value: string
  label: string
  detail: string | null
}

/** A boolean for a `toggle`, a string or null for a `select`, a string array for a `multiSelect`. */
export type SettingValue = boolean | string | string[] | null

/**
 * One setting, described well enough to draw without knowing the provider.
 *
 * `options` is null in two different circumstances and the difference matters: a
 * `toggle` has no list to offer, and either of the other kinds carries null when
 * the provider could not be reached. An empty array is a provider that really has
 * nothing to offer. Never treat null as empty — the saved `value` is still what
 * the backend is using.
 */
export type SettingField = {
  key: string
  kind: 'toggle' | 'select' | 'multiSelect'
  label: string
  help: string | null
  options: SettingOption[] | null
  /** The cap on a `multiSelect`, or null. Enforced here as well as on the server. */
  maxValues: number | null
  required: boolean
  value: SettingValue
}

export type IntegrationSettings = {
  provider: string
  fields: SettingField[]
}

/** `enabled` is always the first field, and every provider has it. */
export const ENABLED_FIELD = 'enabled'

/**
 * A key that belongs to nobody: the switch sent something other than a boolean, or
 * a value sent to a provider that describes no settings of its own.
 */
export const SETTING_UNKNOWN = 'setting_unknown'

/** A value the provider itself refuses, once Kairos's own checks have already passed. */
export const SETTING_INVALID = 'setting_invalid'

/** More values than the field's `maxValues` allows. */
export const SETTING_TOO_MANY = 'setting_too_many'

/** One provider's settings. 404 for a provider nobody has connected. */
export function readIntegrationSettings(
  provider: string,
  signal?: AbortSignal,
): Promise<IntegrationSettings> {
  return request<IntegrationSettings>(
    `/admin/integrations/${encodeURIComponent(provider)}/settings`,
    { signal },
  )
}

/**
 * The whole map, not a patch: the panel has one Save and sends what it is
 * showing. Answers 204, so nothing comes back — re-read after saving, because
 * options move when values do.
 */
export function saveIntegrationSettings(
  provider: string,
  values: Record<string, SettingValue>,
  signal?: AbortSignal,
): Promise<void> {
  return requestNothing(`/admin/integrations/${encodeURIComponent(provider)}/settings`, {
    method: 'PUT',
    headers: JSON_HEADERS,
    body: JSON.stringify({ values }),
    signal,
  })
}
