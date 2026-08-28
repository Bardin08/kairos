import { useEffect, useState, useSyncExternalStore, type ReactElement } from 'react'
import { Link, Redirect, Route, Switch, useLocation } from 'wouter'

import { getAlerts, type Alert } from './adminAlerts'
import { getSettings } from './adminSettings'
import { dismissedFor, forget, remember } from './conditionDismissal'
import ConditionBanner from './ConditionBanner'
import ConditionRow from './ConditionRow'
import Availability from './Availability'
import Invites from './Invites'
import Links from './Links'
import Locked from './Locked'
import Meetings from './Meetings'
import NotOperator from './NotOperator'
import PatternEditor from './PatternEditor'
import Settings from './Settings'
import { refusal, subscribeToRefusal } from './session'

/**
 * One place the operator can be, and the icon that stands for it.
 *
 * Paths are relative: the shell is mounted with `nest`, so wouter's base is
 * `/admin` and both `<Link>` and `<Route>` prefix it. `/admin/links` here would
 * produce `/admin/admin/links`.
 */
type Destination = {
  path: string
  label: string
  icon: ReactElement
}

/**
 * Three, and no fourth. These are the rail on desktop and the whole tab bar grid
 * on a phone, and `.tabbar-inner` is `repeat(3, 1fr)` in kairos.css — a fourth
 * entry wraps into a ragged second row. Settings is not one; see SETTINGS.
 */
const DESTINATIONS: Destination[] = [
  {
    path: '/links',
    label: 'Links',
    icon: (
      <svg
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.6"
        strokeLinecap="round"
        aria-hidden="true"
      >
        <path d="M10.5 13.5a4.5 4.5 0 006.4 0l2.6-2.6a4.5 4.5 0 00-6.4-6.4l-1 1" />
        <path d="M13.5 10.5a4.5 4.5 0 00-6.4 0l-2.6 2.6a4.5 4.5 0 006.4 6.4l1-1" />
      </svg>
    ),
  },
  {
    path: '/availability',
    label: 'Availability',
    icon: (
      <svg
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.6"
        strokeLinecap="round"
        aria-hidden="true"
      >
        <rect x="3.5" y="5.5" width="17" height="15" rx="2.5" />
        <path d="M3.5 10h17M8.5 3v4M15.5 3v4" />
      </svg>
    ),
  },
  {
    path: '/meetings',
    label: 'Meetings',
    icon: (
      <svg
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.6"
        strokeLinecap="round"
        aria-hidden="true"
      >
        <path d="M17.5 20.5v-1.8a3.6 3.6 0 00-3.6-3.6H7.1a3.6 3.6 0 00-3.6 3.6v1.8" />
        <circle cx="10.5" cy="7.5" r="3.6" />
        <path d="M20.5 20.5v-1.8a3.6 3.6 0 00-2.7-3.5" />
      </svg>
    ),
  },
]

/**
 * The secondary destination. The organizer's timezone, the four global limit
 * defaults and Part B's integrations live behind it, all visited about twice a
 * year — not a fourth daily destination, so it does not take a third of the
 * phone's tab bar. It sits at the foot of the rail, under the hairline.
 */
const SETTINGS: Destination = {
  path: '/settings',
  label: 'Settings',
  icon: (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.6"
      strokeLinecap="round"
      aria-hidden="true"
    >
      <path d="M4 7.5h9M17.5 7.5h2.5M4 16.5h3.5M12 16.5h8" />
      <circle cx="15" cy="7.5" r="2.5" />
      <circle cx="9.5" cy="16.5" r="2.5" />
    </svg>
  ),
}

const HOME = DESTINATIONS[0].path

/** Ties the collapsed marker to the banner region it restores, for `aria-controls`. */
const CONDITIONS = 'conditions'

/** Two letters at most, and never more than the name actually has. */
const INITIALS = 2

/**
 * `Vlad Bardin` → `VB`. The avatar is `aria-hidden`, so this is decoration
 * beside the name rather than a second way of reading it.
 */
function initials(name: string): string {
  return name
    .split(/\s+/)
    .filter((word) => word !== '')
    .slice(0, INITIALS)
    .map((word) => word[0]?.toUpperCase() ?? '')
    .join('')
}

/**
 * A destination as a link, marked when it is the one being shown.
 *
 * `aria-current="page"` is the only marker, and both visual channels hang off
 * it: in the rail an inset fill and a heavier weight, in the tab bar the accent
 * AND a 2px edge bar above the tab. Colour is never the only channel.
 */
function destinationLink(destination: Destination, current: string) {
  return (
    <Link
      key={destination.path}
      href={destination.path}
      aria-current={destination.path === current ? 'page' : undefined}
    >
      {destination.icon}
      {destination.label}
    </Link>
  )
}

/**
 * The operator's workbench: a rail on desktop, a bottom tab bar on a phone.
 *
 * Which is drawn is kairos.css's decision alone — `.rail` and `.tabbar` swap at
 * 900px in a media query, and both are always rendered. No width is read here:
 * layout set in JavaScript stops surviving a resize with JS busy, and the
 * breakpoint would exist in two places that could disagree.
 */
export default function AdminShell() {
  // Relative to the mount point, so `/admin/links` reads as `/links` and
  // matches DESTINATIONS without any prefix arithmetic.
  const [current] = useLocation()

  // Whether any admin call has been refused, and which refusal it was.
  // `useSyncExternalStore` rather than state plus an effect because a refusal
  // can land in the window between render and subscribe, and this is the hook
  // that cannot miss it.
  const refused = useSyncExternalStore(subscribeToRefusal, refusal)

  // Whatever the identity provider called this operator, or null. Null is the
  // ordinary case for a provider that sends no `name` claim, and it draws no
  // identity block at all — a subject identifier presented as a person's name
  // is worse than no name. A failed read is the same as no name: the rail is
  // navigation, and nothing about it should turn red because a fetch did.
  const [operator, setOperator] = useState<string | null>(null)

  // What is broken, re-read on every navigation. That is what makes "a visible
  // alert on every admin screen within one page load" true without a poll: the
  // operator cannot reach a screen without this having been asked for it.
  const [alerts, setAlerts] = useState<Alert[]>([])

  // Loud or quiet. Re-derived from storage for every arriving set rather than
  // carried across them, because a set that gained an alert has to reopen — see
  // conditionDismissal.
  const [dismissed, setDismissed] = useState(false)

  useEffect(() => {
    const controller = new AbortController()

    getAlerts(controller.signal)
      .then((arrived) => {
        setAlerts(arrived)

        // A condition that clears and later returns must be loud again, so a
        // stored signature is never allowed to outlive the conditions it named.
        if (arrived.length === 0) forget()

        setDismissed(dismissedFor(arrived))
      })
      // Silent, exactly like the settings read below — least of all the control
      // whose whole job is to be believed when it does turn red.
      .catch(() => {})

    return () => controller.abort()
  }, [current])

  const collapse = () => {
    remember(alerts)
    setDismissed(true)
  }

  const expand = () => {
    forget()
    setDismissed(false)
  }

  useEffect(() => {
    const controller = new AbortController()

    getSettings(controller.signal)
      .then((settings) => setOperator(settings.signedInAs))
      .catch(() => {
        // The pages inside report their own failures, and each of them makes
        // the same call this one did.
      })

    return () => controller.abort()
  }, [])

  // Everything below is chrome, and none of it may be drawn once a call has been
  // refused. Not hidden — absent: a tabbable rail, a New link button that cannot
  // succeed and an empty state asserting there is nothing to show are each a lie.
  // After the hooks above, because the read that discovers the refusal is one.
  if (refused === 'no-session') return <Locked />
  if (refused === 'not-an-operator') return <NotOperator />

  return (
    <>
      <div className="admin-shell">
        <aside className="rail">
          <span className="wordmark">
            <span className="wordmark-glyph" aria-hidden="true">
              <svg
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                strokeWidth="2.2"
                strokeLinecap="round"
              >
                <circle cx="12" cy="12" r="9" />
                <path d="M12 7.5V12l3 1.8" />
              </svg>
            </span>
            Kairos
          </span>

          <nav className="nav" aria-label="Sections">
            {DESTINATIONS.map((destination) => destinationLink(destination, current))}
          </nav>

          {/* `.rail-foot` pins itself to the bottom and draws the hairline that
              says "not one of the three". The inner `.nav` is a div, not a
              second navigation landmark: it is only there because `.nav a` keeps
              a link off the accent colour the base `a` rule would give it.

              It is a flex row, so the identity and the link stack inside one
              `.grow` child — 240px does not hold an avatar, a name and a
              labelled link on one line. */}
          <div className="rail-foot">
            <div className="grow">
              <ConditionRow
                count={dismissed ? alerts.length : 0}
                controls={CONDITIONS}
                onExpand={expand}
              />
              {operator !== null && (
                <div className="row gap-2 mb-3">
                  <span className="avatar avatar-sm" aria-hidden="true">
                    {initials(operator)}
                  </span>
                  <span className="grow t-sm muted">{operator}</span>
                </div>
              )}
              <div className="nav">{destinationLink(SETTINGS, current)}</div>
            </div>
          </div>
        </aside>

        <div className="admin-main">
          {/* The wrapper carries the id rather than the banner: there may be two
              banners or none, and `aria-controls` needs one stable target. */}
          <div id={CONDITIONS}>
            <ConditionBanner alerts={dismissed ? [] : alerts} onDismiss={collapse} />
          </div>
          <Switch>
            {/* The specific path first, by convention rather than by need:
                measured against wouter 3.10, a route path matches in full, so
                neither of these two swallows the other from either position.
                What would make the order matter is `nest` or a wildcard, and
                `the invites path renders the personal links` catches that. */}
            <Route path="/links/invites" component={Invites} />
            <Route path="/links" component={Links} />
            {/* Ahead of the list route by the same convention, and for the same
                reason it is only a convention: `/availability` does not match
                `/availability/<id>` either. */}
            <Route path="/availability/:patternId" component={PatternEditor} />
            <Route path="/availability" component={Availability} />
            <Route path="/meetings" component={Meetings} />
            <Route path="/settings" component={Settings} />
            <Route path="/">
              <Redirect to={HOME} replace />
            </Route>
            <Route>
              <p className="prose">This link does not lead anywhere.</p>
            </Route>
          </Switch>
        </div>
      </div>

      {/* Phone only, by media query. Settings sits outside `.tabbar-inner`
          because that grid is three equal columns and a fourth child would drop
          into a second row a third of the way across; outside it, the secondary
          destination reads as a row of its own and the three primaries keep the
          bottom edge, where the thumb is. */}
      <nav className="tabbar" aria-label="Sections">
        <ConditionRow
          count={dismissed ? alerts.length : 0}
          controls={CONDITIONS}
          onExpand={expand}
        />
        {destinationLink(SETTINGS, current)}
        <div className="tabbar-inner">
          {DESTINATIONS.map((destination) => destinationLink(destination, current))}
        </div>
      </nav>
    </>
  )
}
