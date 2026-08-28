import { Link, useLocation } from 'wouter'

/**
 * One view of the Links section, and the path that holds it.
 *
 * Paths are relative for the reason AdminShell's DESTINATIONS are: the shell is
 * mounted with `nest`, so wouter's base is `/admin` and both `<Link>` and
 * `<Route>` prefix it. `/admin/links` here would produce `/admin/admin/links`.
 */
type View = {
  path: string
  label: string
}

/**
 * Two views of one section, not a fourth rail destination. The rail and the tab
 * bar carry three and no fourth — `.tabbar-inner` is `repeat(3, 1fr)` — and
 * personal links are a second question about links rather than a fourth place
 * the operator lives.
 *
 * The labels are the two pages' own `<h1>`s, so the switch and the heading it
 * lands on never disagree about what a view is called.
 */
const VIEWS: View[] = [
  { path: '/links', label: 'Links' },
  { path: '/links/invites', label: 'Personal links' },
]

/**
 * The switch between the links table and the personal links on it.
 *
 * Links, not buttons that navigate: a view here is a page with its own URL, so
 * middle-click, cmd-click, open-in-new-tab and copy-link-address all have to
 * keep working, and none of the four can be reimplemented on a button. That is
 * also why the marker is `aria-current="page"` — `aria-pressed` would announce a
 * page as a pressed toggle. `.segmented` paints all three in one rule.
 *
 * No heading: both pages own a `page-header` with an `<h1>` already, and this
 * sits inside it, beside that page's primary action.
 *
 * Sized to its labels, and separated from the Open/All filter below by position
 * rather than by scale. It once wore `.segmented-equal` and spanned the content
 * width, which made the two unmistakably different controls; grouped into the
 * header row beside the primary action it is the same size as the filter, and
 * what tells them apart is that one sits in the header and the other sits above
 * the table it filters. That is the weaker separator, and it is the price of
 * getting the switch out of its own full-width band.
 */
export default function SectionNav() {
  const [current] = useLocation()

  return (
    <nav className="segmented" aria-label="Links section">
      {VIEWS.map((view) => (
        <Link
          key={view.path}
          href={view.path}
          aria-current={view.path === current ? 'page' : undefined}
        >
          {view.label}
        </Link>
      ))}
    </nav>
  )
}
