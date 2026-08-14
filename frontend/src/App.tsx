import { Route, Switch } from 'wouter'
import Book from './pages/Book'
import Manage from './pages/Manage'

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
      <Route>This link does not lead anywhere.</Route>
    </Switch>
  )
}
