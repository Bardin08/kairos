import { Route, Switch } from 'wouter'

import { LANDING_PATH, PRIVACY_PATH, TERMS_PATH } from '../../lib/legal'
import Landing from './Landing'
import Privacy from './Privacy'
import Terms from './Terms'

/** The lazy chunk's root, so three pages of text stay out of the booking route's bundle. */
export default function LegalShell() {
  return (
    <Switch>
      <Route path={PRIVACY_PATH} component={Privacy} />
      <Route path={TERMS_PATH} component={Terms} />
      <Route path={LANDING_PATH} component={Landing} />
    </Switch>
  )
}
