import { lazy, Suspense } from 'react'
import { Route, Switch } from 'wouter'
import Book from './pages/Book'
import Integrations from './pages/Integrations'
import Manage from './pages/Manage'

// The workbench is split out of the entry chunk deliberately. An invitee opens
// /book/:slug from a link somebody pasted into a message, on a phone, on mobile
// data, and has no use for the operator's admin surface — every byte of it
// downloaded on that route is a byte spent against the bundle budget for a
// screen that reader will never see. `lazy` is what keeps it in its own file:
// import it statically and Rollup folds all four admin pages back into the
// booking route's bundle.
const AdminShell = lazy(() => import('./routes/admin/AdminShell'))

export default function App() {
  return (
    <Switch>
      <Route path="/book/:slug" component={Book} />
      {/* Keyed on the manage token, not on the booking's id. Every manage route
          the API ships takes the token as its path segment and accepts no id at
          all, and the token is the capability anyway: it is minted once, stored
          only as a SHA-256 hash, and never handed back after the 201 that
          created the booking. A `/manage/:id?token=` shape would need a second
          identifier the server has no use for, and would leak the token into
          Referer headers and server logs as a query string. This is the URL the
          booking page shows after a booking; the two have to agree or nothing
          is manageable. */}
      <Route path="/manage/:token" component={Manage} />
      {/* Fixed, and not a preference: IntegrationEndpoints.ReturnPath is this
          string, and every OAuth callback redirects to it carrying
          ?connected= or ?error=. Rename this route and the provider bounces a
          browser onto the not-found page halfway through a connection. */}
      <Route path="/integrations" component={Integrations} />
      {/* `nest` makes /admin the base for everything the shell routes, so the
          shell's own paths stay relative and it can be mounted somewhere else
          without editing four <Route>s inside it. The fallback is nothing at
          all: the chunk is a same-origin file already being fetched, and a
          spinner that appears for one frame is worse than a frame of the page
          it is about to become. */}
      <Route path="/admin" nest>
        <Suspense fallback={null}>
          <AdminShell />
        </Suspense>
      </Route>
      <Route>This link does not lead anywhere.</Route>
    </Switch>
  )
}
