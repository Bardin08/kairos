import AlertIcon from '../../components/AlertIcon'
import { asApiError } from '../../lib/api'

/**
 * An expired session, which is the one failure the workbench can do something
 * about. Everything else is the server's own sentence.
 */
const UNAUTHORIZED = 401

/**
 * A server route under a path the single page nests on, so it is reached with a
 * plain anchor and never with a wouter `<Link>` — the router would swallow the
 * navigation and render its own not-found instead. It issues the OIDC challenge
 * and comes back to /admin; there is no login form anywhere in this
 * application.
 */
export const SIGN_IN = '/admin/login'

/**
 * One failed request, said in the operator's terms, in the one place every
 * admin page routes its failures through.
 *
 * A 401 is not an error to read and shrug at — it means the cookie expired
 * while the tab was open, which on a workbench left open overnight is the most
 * ordinary failure there is. Rendered generically it reads "The server answered
 * 401.", which names no recovery at all. So it gets the recovery instead.
 *
 * Shared rather than repeated because the alternative measured badly: four
 * pages each deciding what a 401 means is four places for the fifth page to
 * forget, and Part C already shipped two different sentences for it.
 *
 * `error` is whatever a `catch` received — never a message already flattened to
 * a string. The status is the whole of what this branches on, and a string has
 * thrown it away.
 */
export default function Failure({
  error,
  className,
}: {
  /** Null renders nothing at all, so a caller needs no conditional of its own. */
  error: unknown
  /** The spacing the surrounding layout wants. The colour and icon are not optional. */
  className?: string
}) {
  if (error === null || error === undefined) return null

  const failed = asApiError(error)
  const classes = className === undefined ? 'field-error' : `field-error ${className}`

  return (
    <p className={classes} role="alert">
      <AlertIcon />
      {failed.status === UNAUTHORIZED ? (
        <span>
          Your session has expired. <a href={SIGN_IN}>Sign in again</a> — nothing on this screen is
          lost by doing it.
        </span>
      ) : (
        failed.message
      )}
    </p>
  )
}
