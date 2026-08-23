// Whether the workbench may still ask anything, and the one place that question
// is answered.
//
// Every admin call goes through `adminRequest` rather than `request`: a refusal
// latches this module before the rejection reaches the page that made the call,
// AdminShell subscribes, and it replaces itself with the screen for that
// refusal. Without that, a refused read leaves the page rendering its own chrome
// around nothing — `/admin/availability` drew its heading, its name field, its
// New pattern button and "No patterns yet" over a refused request, and an
// operator with fifty patterns read that as fifty patterns gone.
//
// Here rather than in lib/api.ts, which is in the invitee's entry chunk. This is
// imported only from behind the lazily loaded admin shell, so Rollup keeps it in
// the admin chunk.

import { asApiError, request, requestNothing } from '../../lib/api'

const UNAUTHORIZED = 401

/**
 * Signed in, and this account is not an operator of this Kairos. Read off the
 * body's code rather than off the 403 alone: a 403 that means something else
 * must not be dressed as this one.
 */
const NOT_AN_OPERATOR = 'not_an_operator'

/**
 * The two ways an admin call is refused, and they are not one state with two
 * wordings. A session that ran out is fixed by signing in; an account this
 * Kairos does not admit is not, and offering that as the way out sends an
 * operator round a loop that ends where it started.
 */
export type Refusal = 'no-session' | 'not-an-operator'

/**
 * Latched, never unlatched. Signing in is a full browser navigation to
 * /admin/login and back, so the way out of either state is a new document; a
 * flag that could be cleared in place would need every page to agree on when.
 */
let refused: Refusal | null = null

const listeners = new Set<() => void>()

/** `useSyncExternalStore`'s two halves, so no refusal can land between them. */
export function subscribeToRefusal(listener: () => void): () => void {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

export function refusal(): Refusal | null {
  return refused
}

function refuse(kind: Refusal): void {
  if (refused !== null) return
  refused = kind
  for (const listener of listeners) listener()
}

/**
 * Marks the session gone if the failure says so, and rethrows either way: the
 * caller's own catch still runs, still clears its pending flag, still aborts
 * cleanly. It simply stops being the thing on screen, because the shell above it
 * is about to unmount it.
 */
async function guard<T>(work: Promise<T>): Promise<T> {
  try {
    return await work
  } catch (error: unknown) {
    const failure = asApiError(error)
    if (failure.status === UNAUTHORIZED) refuse('no-session')
    else if (failure.code === NOT_AN_OPERATOR) refuse('not-an-operator')
    throw error
  }
}

export function adminRequest<T>(path: string, init?: RequestInit): Promise<T> {
  return guard(request<T>(path, init))
}

/** The 204 case. Same guard, no body to parse. */
export function adminRequestNothing(path: string, init?: RequestInit): Promise<void> {
  return guard(requestNothing(path, init))
}

/**
 * Where to send an operator who has no session.
 *
 * Read from the server rather than compiled in: one bundle is deployed
 * everywhere, and a deployment that wants the operator through a portal first
 * sets Authentication__SignInUrl without rebuilding anything. The route is
 * anonymous, so this call deliberately does NOT go through `guard` — a 401 from
 * it would mean the route is broken, not the session.
 */
export function getSignInUrl(signal?: AbortSignal): Promise<string> {
  return request<{ signInUrl: string }>('/sign-in', { signal }).then(
    (answer) => answer.signInUrl,
  )
}
