import AlertIcon from '../../components/AlertIcon'
import { asApiError } from '../../lib/api'

/**
 * One failed request, said in the operator's terms, for every admin page.
 *
 * No case for a 401, deliberately. It used to print "Your session has expired."
 * above a screen still drawing its own heading, buttons and empty state. A
 * refused request now latches session.ts and AdminShell replaces itself with
 * `Locked`, so this component is unmounted first.
 *
 * `error` is whatever a `catch` received, never a message already flattened to a
 * string: `asApiError` turns an aborted fetch, a dead network and a non-JSON 200
 * into one sentence a reader can act on.
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
