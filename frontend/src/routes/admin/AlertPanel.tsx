import { useState } from 'react'

import AlertIcon from '../../components/AlertIcon'
import type { Alert } from './adminAlerts'

/**
 * The code the integrations source publishes, and the one detail key it
 * documents. Both live here because copy lives here: every other admin surface
 * in this application turns a code into a sentence on this side, and prose from
 * the server is prose no designer ever sees.
 */
const RECONNECT_REQUIRED = 'integration_reconnect_required'
const ACCOUNT = 'account'

/** When the alert names nothing at all. Better than a sentence with a hole in it. */
const SOMETHING = 'a connected account'

/** The action's label. The link leaves the shell entirely; see the anchor below. */
const FIX = 'Fix this'

/**
 * What the alert says, out of its code and whatever it carried with it.
 *
 * `details` supplies the values, this supplies the sentence around them. A code
 * this build has never heard of still says something true — the panel is the
 * operator's only channel, so an unknown alert must not be an invisible one.
 */
function sentence(alert: Alert): string {
  const who = alert.details[ACCOUNT] ?? alert.subject ?? SOMETHING

  if (alert.code === RECONNECT_REQUIRED) {
    return `Kairos can no longer reach ${who}. Bookings are still being accepted and are not reaching the calendar.`
  }

  return `Something needs attention: ${who}.`
}

/** `2026-08-19T07:41:12Z` → `19 Aug 2026`, or null when nothing was ever proven. */
function lastWorked(since: string | null): string | null {
  if (since === null) return null

  return new Date(since).toLocaleDateString(undefined, {
    day: 'numeric',
    month: 'short',
    year: 'numeric',
  })
}

/**
 * Everything that is broken, as an indicator with a panel behind it.
 *
 * It renders nothing at all when nothing is wrong. A permanent bell that is empty
 * 364 days a year is furniture, and an operator stops seeing furniture — which is
 * the failure this whole surface exists to fix, one layer up.
 *
 * Mounted twice by AdminShell: in the rail foot on desktop, and in the tab bar's
 * outer row on a phone. `.tabbar-inner` is three equal columns and a fourth child
 * drops into a ragged second row, which is why this sits beside Settings instead.
 */
export default function AlertPanel({ alerts }: { alerts: Alert[] }) {
  const [open, setOpen] = useState(false)

  if (alerts.length === 0) return null

  const label = `${alerts.length} ${alerts.length === 1 ? 'problem needs' : 'problems need'} attention`

  return (
    <div className="alertpanel">
      {/* The count is written into the accessible name and drawn as a number
          beside a glyph, never as a coloured dot on its own: DESIGN.md's Second
          Channel Rule, and both channels survive a monochrome screenshot. */}
      <button
        type="button"
        className="icon-btn"
        aria-expanded={open}
        aria-controls="alertpanel-list"
        aria-label={label}
        onClick={() => setOpen(!open)}
      >
        <AlertIcon />
        <span className="badge">{alerts.length}</span>
      </button>

      {/* Announced without stealing focus: the operator may be typing in the
          editor when a page load discovers this. */}
      <div id="alertpanel-list" role="status" aria-live="polite" hidden={!open}>
        {open && (
          <div className="card card-pad-sm">
            <div className="rowgroup">
              {alerts.map((alert, index) => {
                const worked = lastWorked(alert.since)

                return (
                  <div key={`${alert.code}-${alert.subject ?? index}`}>
                    <p className="prose">{sentence(alert)}</p>
                    {worked !== null && <p className="fine">Last worked {worked}.</p>}
                    {/* A plain anchor and never a wouter Link: /integrations is
                        outside the shell's router, so a push would not reach it.
                        The href is the alert's own — a constant here would be
                        right for one publisher and wrong for the next. */}
                    {alert.link !== null && (
                      <a className="btn btn-inline" href={alert.link}>
                        {FIX}
                      </a>
                    )}
                  </div>
                )
              })}
            </div>
          </div>
        )}
      </div>
    </div>
  )
}
