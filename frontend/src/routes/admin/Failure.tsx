import AlertIcon from '../../components/AlertIcon'
import { asApiError } from '../../lib/api'

/**
 * One failed request, said in the operator's terms, in the one place every
 * admin page routes its failures through.
 *
 * It no longer has a case for a 401, and losing it was the point. This
 * component used to print "Your session has expired. Sign in again" above a
 * screen still drawing its own heading, its own buttons and an empty state
 * claiming there was nothing to show — a banner apologising for chrome that
 * should not have been there. A refused request now latches session.ts and
 * AdminShell replaces itself with `Locked`, so this component is unmounted
 * before it could render anything about it. What is left here is every OTHER
 * failure, which is the server's own sentence and always was.
 *
 * Shared rather than repeated because the alternative measured badly: four
 * pages each deciding what a failure means is four places for the fifth page to
 * forget.
 *
 * `error` is whatever a `catch` received — never a message already flattened to
 * a string. `asApiError` is what turns an aborted fetch, a dead network and a
 * non-JSON 200 into one sentence a reader can act on, and a string has thrown
 * all of that away before it arrives.
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

  const classes = className === undefined ? 'field-error' : `field-error ${className}`

  return (
    <p className={classes} role="alert">
      <AlertIcon />
      {asApiError(error).message}
    </p>
  )
}
