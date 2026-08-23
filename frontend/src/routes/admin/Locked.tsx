import { useEffect, useState } from 'react'

import { getSignInUrl } from './session'

/**
 * The whole screen when there is no session — and it is the whole screen on
 * purpose.
 *
 * It replaces AdminShell rather than appearing inside it, so there is no rail,
 * no tab bar, no page heading, no button and no empty state anywhere in the
 * document. Chrome drawn around a refused request invites actions that cannot
 * succeed, and an empty state under it asserts something the page never learned:
 * "No patterns yet" is a sentence about the data, a 401 is a sentence about
 * permission.
 *
 * One wording covers both a session that expired and a browser that never had
 * one. Nothing here can tell them apart honestly — the cookie handler answers
 * 401 to a missing cookie and to an expired one alike — so it says what is true
 * in both cases rather than guessing, which is what the old "Your session has
 * expired." did to somebody who had never signed in.
 *
 * No new CSS: `.page`, `.card`, `.card-narrow`, `.card-pad`, `.title-page`,
 * `.prose` and `.btn` are all already in kairos.css, the global `:focus-visible`
 * rule paints the ring, and every colour comes from the theme tokens.
 */
export default function Locked() {
  // Null until the anonymous route answers. No fallback constant: baking
  // /admin/login in here would put deployment configuration into a bundle built
  // once and deployed everywhere, which is the thing the route exists to avoid.
  const [signInUrl, setSignInUrl] = useState<string | null>(null)
  const [unreachable, setUnreachable] = useState(false)

  useEffect(() => {
    const controller = new AbortController()

    getSignInUrl(controller.signal)
      .then(setSignInUrl)
      .catch(() => {
        if (controller.signal.aborted) return
        setUnreachable(true)
      })

    return () => controller.abort()
  }, [])

  return (
    <div className="page">
      <main className="card card-narrow card-pad mt-7">
        <h1 className="title-page mb-3">Sign in to continue</h1>
        <p className="prose t-sm">
          The workbench needs a signed-in session. If you had one, it has run out — nothing you set
          up is affected, and signing in again brings all of it back.
        </p>

        {/* A plain anchor, never a wouter `<Link>`: the sign-in URL is a server
            route under the path the single page nests on, or another origin
            entirely, and the router would swallow the navigation and render its
            own not-found. Drawn only once the URL is known — a link with nowhere
            to go is worse than a moment without one. */}
        {signInUrl !== null && (
          <a className="btn mt-5" href={signInUrl}>
            Sign in
          </a>
        )}

        {unreachable && (
          <p className="prose t-sm mt-5">
            The server could not be reached to find out where to sign in. Reload this page once it
            is back.
          </p>
        )}
      </main>
    </div>
  )
}
