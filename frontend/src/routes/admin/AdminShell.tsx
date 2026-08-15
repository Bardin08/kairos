import { useEffect, useState, useSyncExternalStore, type ReactElement } from 'react'
import { Link, Redirect, Route, Switch, useLocation } from 'wouter'

import { getSettings } from './adminSettings'
import Availability from './Availability'
import Links from './Links'
import Locked from './Locked'
import Meetings from './Meetings'
import Settings from './Settings'
import { isLocked, subscribeToLock } from './session'

/**
 * One place the operator can be, and the icon that stands for it.
 *
 * Paths are relative: the shell is mounted with `nest`, so wouter's base is
 * `/admin` and both `<Link>` and `<Route>` prefix it. Writing `/admin/links`
 * here would produce `/admin/admin/links`.
 */
type Destination = {
  path: string
  label: string
  icon: ReactElement
}

/**
 * Three, and no fourth.
 *
 * These are the rail on desktop and the whole of the tab bar's grid on a phone,
 * and `.tabbar-inner` is `repeat(3, 1fr)` in kairos.css — a fourth entry here
 * wraps into a ragged second row. Settings is deliberately not one of them; see
 * SETTINGS.
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
 * The secondary destination.
 *
 * The organizer's timezone, the four global limit defaults and Part B's
 * integrations all live behind it, and all of them are visited about twice a
 * year. That is not a fourth daily destination, so it does not take a third of
 * the phone's tab bar: it sits at the foot of the rail, under the hairline, and
 * above the tab grid on a phone.
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
 * `aria-current="page"` is the only thing that marks it, and it is what both
 * visual channels hang off: in the rail kairos.css answers it with an inset
 * fill and a heavier weight, in the tab bar with the accent AND a 2px edge bar
 * above the tab. Colour is never the only channel, which is the rule this
 * control has already broken twice.
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
 * Which of the two is drawn is kairos.css's decision alone — `.rail` and
 * `.tabbar` swap at 900px in a media query, and both are always rendered. No
 * width is read here and none should be: layout set in JavaScript stops
 * surviving a resize with JS busy, and the breakpoint would then exist in two
 * places that could disagree.
 */
export default function AdminShell() {
  // Relative to the mount point, so `/admin/links` reads as `/links` and
  // matches DESTINATIONS without any prefix arithmetic.
  const [current] = useLocation()

  // Whether any admin call has been refused. `useSyncExternalStore` rather than
  // state plus an effect because a 401 can land in the window between render and
  // subscribe — the shell's own read below is fired from an effect, and so is
  // every page's — and this is the hook that cannot miss it.
  const locked = useSyncExternalStore(subscribeToLock, isLocked)

  // Whatever the identity provider called this operator, or null. Null is the
  // ordinary case for a provider that sends no `name` claim, and it draws no
  // identity block at all — a subject identifier presented as a person's name
  // is worse than no name. A failed read is the same as no name: the rail is
  // navigation, and nothing about it should turn red because a fetch did.
  const [operator, setOperator] = useState<string | null>(null)

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

  // Everything below this line is chrome, and none of it may be drawn without a
  // session. Not hidden — absent: a rail the operator can tab into, a New link
  // button that cannot succeed and an empty state asserting there is nothing to
  // show are each a lie told by a screen that was refused permission to ask.
  // Placed after the hooks above, which run unconditionally, because the read
  // that discovers the refusal is one of them.
  if (locked) return <Locked />

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
              second navigation landmark: one link is not a set of them, and it
              is only there because `.nav a` is what keeps a link off the
              accent colour it would otherwise inherit from the base `a` rule.

              `.rail-foot` is a flex row, so the identity and the link are
              stacked inside one `.grow` child rather than laid side by side —
              240px does not hold an avatar, a name and a labelled link on one
              line without one of them being cut. */}
          <div className="rail-foot">
            <div className="grow">
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
          <Switch>
            <Route path="/links" component={Links} />
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
        {destinationLink(SETTINGS, current)}
        <div className="tabbar-inner">
          {DESTINATIONS.map((destination) => destinationLink(destination, current))}
        </div>
      </nav>
    </>
  )
}
