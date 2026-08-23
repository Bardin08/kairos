import AlertIcon from '../../components/AlertIcon'
import type { Alert } from './adminAlerts'
import { copyFor, lastWorked } from './conditionCopy'

/**
 * The loud state of the condition surface: what is broken, said where the operator
 * is already reading.
 *
 * In the content column rather than anchored to a control in the rail, and that is
 * the whole reason it replaced a popover: the content column exists at every
 * width, so the phone layout needs no second design and nothing has to escape a
 * scroll container to be legible.
 *
 * Nothing is rendered when nothing is wrong.
 */
export default function ConditionBanner({
  alerts,
  onDismiss,
}: {
  alerts: Alert[]
  onDismiss: () => void
}) {
  if (alerts.length === 0) return null

  return (
    <>
      {alerts.map((alert, index) => {
        const copy = copyFor(alert)
        const worked = lastWorked(alert.since)

        return (
          <div
            // `status` and `polite`, never `alert` and assertive: this is a
            // condition that has usually been true for minutes already, and
            // interrupting whatever the operator is typing to say so would be the
            // wrong trade.
            role="status"
            aria-live="polite"
            className="banner"
            key={`${alert.code}-${alert.subject ?? index}`}
          >
            <span className="banner-mark" aria-hidden="true">
              <AlertIcon />
            </span>

            <div className="grow">
              <p className="banner-title">{copy.title}</p>
              <p className="t-sm muted">
                {copy.body}
                {worked !== null && ` Last worked ${worked}.`}
              </p>
            </div>

            {/* A plain anchor and never a wouter Link: /integrations is outside the
                shell's router, so a push would not reach it. The href is the
                alert's own — a constant here would be right for one publisher and
                wrong for the next. */}
            {alert.link !== null && (
              <a className="btn btn-inline" href={alert.link}>
                {copy.action}
              </a>
            )}

            {/* The consequence is spoken rather than implied by a cross: this
                control collapses the surface, it never silences it. It collapses
                the whole set, because a dismissal is a property of the set — see
                conditionDismissal's signature. */}
            <button
              type="button"
              className="icon-btn"
              aria-label="Dismiss — keeps a marker in the sidebar"
              onClick={onDismiss}
            >
              <svg
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                strokeWidth="1.8"
                strokeLinecap="round"
                aria-hidden="true"
              >
                <path d="M6 6l12 12M18 6L6 18" />
              </svg>
            </button>
          </div>
        )
      })}
    </>
  )
}
