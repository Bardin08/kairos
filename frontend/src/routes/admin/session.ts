// Whether the workbench still has a session, and the one place that question is
// answered.
//
// Every admin call goes through `adminRequest` rather than through `request`
// directly, and the difference is one branch: a 401 latches this module before
// the rejection reaches the page that made the call. AdminShell subscribes and
// replaces itself with the locked screen, so a refused read never leaves a page
// rendering its own chrome around nothing — which is the bug this file exists
// for. `/admin/availability` used to draw its heading, its name field, its New
// pattern button and "No patterns yet" over a request that was refused
// permission to ask, and an operator with fifty patterns read that as fifty
// patterns gone.
//
// Here rather than in lib/api.ts for the same measured reason as its siblings:
// lib/api.ts is in the invitee's entry chunk, this is imported only from behind
// the lazily loaded admin shell, and Rollup keeps it in the admin chunk.

import { asApiError, request, requestNothing } from '../../lib/api'

/** The one status that means "no session", from any admin route, at any time. */
const UNAUTHORIZED = 401

/**
 * Latched, never unlatched. Signing in is a full browser navigation to
 * /admin/login and back, so the way out of this state is a new document — and a
 * flag that could be cleared in place would need every page to agree on when,
 * which is the coordination this module exists to remove.
 */
let locked = false

const listeners = new Set<() => void>()

/** `useSyncExternalStore`'s two halves, so no 401 can land between them. */
export function subscribeToLock(listener: () => void): () => void {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

export function isLocked(): boolean {
  return locked
}

function lock(): void {
  if (locked) return
  locked = true
  for (const listener of listeners) listener()
}

/**
 * Marks the session gone if the failure says so, and rethrows either way.
 *
 * The rejection is never swallowed: the caller's own catch still runs, still
 * clears its pending flag, still aborts cleanly. It simply stops being the
 * thing on screen, because the shell above it is about to unmount it.
 */
async function guard<T>(work: Promise<T>): Promise<T> {
  try {
    return await work
  } catch (error: unknown) {
    if (asApiError(error).status === UNAUTHORIZED) lock()
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
 * Read from the server rather than compiled in, because it is deployment
 * configuration: one bundle is built and deployed everywhere, and a deployment
 * that wants the operator through a portal first sets Authentication__SignInUrl
 * without rebuilding anything. The route is anonymous — a signed-out operator is
 * its only reader — so this call deliberately does NOT go through `guard`; a 401
 * from it would mean something has gone wrong with the route rather than with
 * the session.
 */
export function getSignInUrl(signal?: AbortSignal): Promise<string> {
  return request<{ signInUrl: string }>('/sign-in', { signal }).then(
    (answer) => answer.signInUrl,
  )
}
