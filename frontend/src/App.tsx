import { lazy, Suspense } from 'react'
import { Route, Switch } from 'wouter'
import Book from './pages/Book'
import Integrations from './pages/Integrations'
import Manage from './pages/Manage'
import Proposal from './pages/Proposal'

// Lazy so the admin surface stays out of the booking route's bundle. Import it
// statically and Rollup folds all four admin pages back in, over budget.
const AdminShell = lazy(() => import('./routes/admin/AdminShell'))

export default function App() {
  return (
    <Switch>
      <Route path="/book/:slug" component={Book} />
      {/* Keyed on the manage token, not on the booking's id. Every manage route
          takes the token as its path segment and accepts no id, and the token is
          the capability anyway: minted once, stored only as a SHA-256 hash.
          `/manage/:id?token=` would need a second identifier and leak no less —
          a Referer carries the whole URL, path included. What keeps the token
          out of a referrer is the `no-referrer` policy the API sends. */}
      <Route path="/manage/:token" component={Manage} />
      {/* Keyed on the PROPOSAL's token, which is not the manage token and
          cannot be exchanged for one: it answers a single question and is
          closed by the answer. The path is BookingMailer.ProposalPath, and the
          mail is the only place this URL is ever built — rename this route and
          every suggestion already sent lands on the not-found page. */}
      <Route path="/proposal/:token" component={Proposal} />
      {/* Fixed, and not a preference: IntegrationEndpoints.ReturnPath is this
          string, and every OAuth callback redirects to it carrying
          ?connected= or ?error=. Rename this route and the provider bounces a
          browser onto the not-found page halfway through a connection. */}
      <Route path="/integrations" component={Integrations} />
      {/* `nest` makes /admin the base for everything the shell routes, so the
          shell's own paths stay relative. The fallback is nothing at all: the
          chunk is a same-origin file already being fetched, and a spinner that
          appears for one frame is worse than a frame of the page it is about to
          become. */}
      <Route path="/admin" nest>
        <Suspense fallback={null}>
          <AdminShell />
        </Suspense>
      </Route>
      <Route>This link does not lead anywhere.</Route>
    </Switch>
  )
}
