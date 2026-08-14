import { Route, Switch } from 'wouter'
import Book from './pages/Book'
import Manage from './pages/Manage'

export default function App() {
  return (
    <Switch>
      <Route path="/book/:slug" component={Book} />
      <Route path="/manage/:id" component={Manage} />
      <Route>This link does not lead anywhere.</Route>
    </Switch>
  )
}
