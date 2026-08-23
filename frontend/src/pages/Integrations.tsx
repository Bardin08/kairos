import { useEffect, useState } from 'react'
import { Link, useSearchParams } from 'wouter'

import AlertIcon from '../components/AlertIcon'
import {
  asApiError,
  connectIntegration,
  CREDENTIAL_UNREADABLE,
  listIntegrations,
  PROVIDER_NOT_CONFIGURED,
  type Integration,
} from '../lib/api'
import { localZone, shortDate, zonedDate, zonedTime } from '../lib/time'

// The only two status shapes kairos.css draws, and the Second Channel Rule is
// why they are shapes: `live` fills the 7px dot, `paused` leaves it hollow at
// the same size. Only a working connection earns the filled one — "not
// configured", "not connected" and "reconnect needed" are all hollow, and the
// words beside the dot are what tells them apart.
const LIVE = 'live'
const PAUSED = 'paused'

const UNAUTHORIZED = 401

// The two the callback puts on this page's URL. It always redirects here and
// always carries one of them, so the operator learns what happened on the page
// they started from rather than on a blank screen.
const CONNECTED_PARAMETER = 'connected'
const ERROR_PARAMETER = 'error'

/**
 * Whether the page has a list to render. `locked` is its own answer rather than
 * a variety of `broken` because the recovery is different: nothing on this page
 * fixes it, and signing in does. See `LOCKED_MESSAGE`.
 */
type Phase = 'loading' | 'ready' | 'locked' | 'broken'

/**
 * What a row says and offers, worked out from the flags alone. No provider name
 * reaches this function: a row describes itself out of `configured` /
 * `connected` / `reconnectRequired`, which lets the page render a provider this
 * build has never heard of.
 */
type Presentation = {
  /** Filled dot, or hollow. Only a working connection fills it. */
  live: boolean
  state: string
  detail: string
  /** The button's label, or null where there is no action to offer. */
  action: string | null
}

/**
 * A server route under a path the single page nests on, so it is reached with a
 * plain anchor and never a wouter `<Link>` — the router would swallow the
 * navigation. It issues the OIDC challenge; there is no login form anywhere.
 */
const SIGN_IN = '/admin/login'

/**
 * Where this page is reached from, and the way back out of it.
 *
 * The route has to stay `/integrations` — `IntegrationEndpoints.ReturnPath` is
 * that string — so this page renders outside the admin shell and inherits none
 * of its chrome. This link is the missing edge, and it is the page's own rather
 * than the browser's Back: the OAuth callback arrives as a redirect from another
 * origin, and the same URL in a fresh tab has no history.
 *
 * A wouter `<Link>` and not an anchor, unlike `SIGN_IN`: /admin/settings is a
 * client route the single page owns, so this is a push and not a reload.
 */
const SETTINGS = '/admin/settings'

/**
 * A 401 here means one thing now, and it has a fix.
 *
 * Until Part C this named `Integrations:AdminToken` and told the operator to
 * clear it from configuration and restart — a stopgap Task 1 deleted with the
 * setting, leaving this page pointing at something that no longer exists.
 */
const LOCKED_MESSAGE =
  'Your session has expired, or you were never signed in — so there is nothing here to read yet.'

/**
 * What the page can offer about a failure, and never more than the failure has.
 *
 * A control that cannot work is worse than none: it invites a press, spends a
 * round trip and returns the same sentence. So `none` is a real answer — a
 * Connect refused with `provider_not_configured` is fixed in a configuration
 * file and a restart.
 */
type Recovery = 'none' | 'sign-in' | 'retry'

/**
 * Said out loud rather than left implied: the alternative is a red sentence
 * describing a fault with nothing beside it, which the operator read as a dead
 * end.
 */
const RETRY_MESSAGE = 'Nothing on this page can put that right. Read it again, and check the server log if it keeps failing.'

/**
 * A provider name as a heading, without knowing any provider. The API sends the
 * lowercase enum name the connect path takes, and capitalising the first letter
 * is the whole transformation: a lookup table would be a list of vendors in the
 * frontend, which the endpoint exists to avoid.
 */
function label(provider: string): string {
  return provider.charAt(0).toUpperCase() + provider.slice(1)
}

function describe(integration: Integration): Presentation {
  // Asked before `configured`, and that order is the point: this row holds a
  // credential that cannot be read, so reconnecting puts something right — not
  // true of the unconfigured row below, where nothing is stored and nothing is
  // broken. The button is offered either way; `connectMessage` says the rest.
  if (integration.fault === CREDENTIAL_UNREADABLE) {
    const missing = integration.configured
      ? ''
      : ' This deployment also holds no client credentials for it, so those have to go into configuration and the process restart before a consent screen can open.'

    return {
      live: false,
      state: 'Reconnect needed',
      detail:
        `Kairos cannot read the credential stored for ${integration.accountLabel ?? 'this provider'}: it was encrypted with a different Security:SecretKey than this deployment runs with now. Connecting again replaces it.${missing}`,
      action: 'Connect again',
    }
  }

  // Asked next, because it outranks everything below it: with no client
  // credentials there is nothing to connect to, and a Connect button here would
  // be a button whose only outcome is `provider_not_configured`.
  if (!integration.configured) {
    return {
      live: false,
      state: 'Not available',
      detail:
        'This deployment holds no client credentials for it. Add them to configuration and restart, and the button appears here.',
      action: null,
    }
  }

  if (!integration.connected) {
    return {
      live: false,
      state: 'Not connected',
      detail: 'Nothing is linked yet. Connecting opens its consent screen.',
      action: 'Connect',
    }
  }

  // Connected and broken: the credential is stored and the provider has stopped
  // honouring it — the grant was revoked in the provider's own account settings,
  // or expired. The same button, because a fresh consent is the only fix there
  // is.
  if (integration.reconnectRequired) {
    return {
      live: false,
      state: 'Reconnect needed',
      detail: `${integration.accountLabel ?? 'The linked account'} stopped being accepted, so nothing is reaching that calendar. Reconnecting is the fix.`,
      action: 'Reconnect',
    }
  }

  return {
    live: true,
    state: 'Connected',
    detail: integration.accountLabel ?? '',
    action: null,
  }
}

/**
 * What the callback's `?error=` means, in the operator's terms.
 *
 * The backend logs the provider's own words and sends a code, on purpose — the
 * raw text is attacker-controllable and arrives on a route anyone can call. So
 * these four sentences are all there is, and each names the recovery.
 */
function calledBackMessage(code: string): string {
  switch (code) {
    case 'consent_denied':
      return 'The consent screen was closed without granting access, so nothing was connected. Press Connect again when you are ready.'
    case 'state_invalid':
      return 'That connection took too long to finish, or came back through a stale tab. Start it again from here.'
    case 'connect_failed':
      return 'The provider was reached but the connection could not be completed. The server log has the reason.'
    default:
      return 'The connection did not complete. Start it again from here.'
  }
}

/** What a refused `connect` means. `code` is null for a 400 with no code. */
function connectMessage(status: number, code: string | null): string {
  if (status === UNAUTHORIZED) return LOCKED_MESSAGE
  if (code === PROVIDER_NOT_CONFIGURED) {
    return 'This deployment has no client credentials for that provider, so there is no consent screen to open. Add them to configuration and restart.'
  }
  return 'That connection could not be started. Try again, and check the server log if it keeps happening.'
}

/** When the connection was made, in this browser's zone. */
function connectedOn(instant: string): string {
  const zone = localZone()
  return `${shortDate(zonedDate(instant, zone))}, ${zonedTime(instant, zone)}`
}

/** The operator's page for connecting a calendar to Kairos. */
export default function Integrations() {
  // Read, never cleared. The banner is derived from the URL rather than copied
  // into state, so a reload shows the same answer the redirect gave and there is
  // no effect racing the fetch to rewrite the address bar.
  const [params] = useSearchParams()

  const [rows, setRows] = useState<Integration[]>([])
  const [phase, setPhase] = useState<Phase>('loading')
  const [failure, setFailure] = useState('')
  const [recovery, setRecovery] = useState<Recovery>('none')
  // Bumped by Try again. The read lives in an effect keyed on this rather than
  // in a handler that repeats it, so one copy of the request keeps one copy of
  // the abort handling — and an operator pressing twice cancels the first read
  // instead of racing it.
  const [attempt, setAttempt] = useState(0)
  // The provider whose Connect is in flight. It is never cleared on success:
  // the browser is leaving for the consent screen, and a button that stops
  // spinning while the page is still there reads as a press that did nothing.
  const [pending, setPending] = useState('')

  useEffect(() => {
    const controller = new AbortController()
    setPhase('loading')
    setFailure('')
    setRecovery('none')

    listIntegrations(controller.signal)
      .then((list) => {
        setRows(list)
        setPhase('ready')
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        const failed = asApiError(error)
        const locked = failed.status === UNAUTHORIZED

        setPhase(locked ? 'locked' : 'broken')
        // The server's own sentence, then what can be done about it. A stored
        // credential that will not decrypt no longer arrives here at all — it
        // comes back on its row with a Connect button beside it — so everything
        // that reaches this branch is a fault the page cannot act on.
        setFailure(locked ? LOCKED_MESSAGE : `${failed.message} ${RETRY_MESSAGE}`)
        setRecovery(locked ? 'sign-in' : 'retry')
      })

    return () => controller.abort()
  }, [attempt])

  async function connect(provider: string) {
    setPending(provider)
    setFailure('')

    try {
      const consentUrl = await connectIntegration(provider)
      // Navigated to, never fetched: another origin's consent screen, and the
      // redirect back to /integrations is what finishes the exchange.
      window.location.assign(consentUrl)
    } catch (error: unknown) {
      const failed = asApiError(error)
      setPending('')
      setFailure(connectMessage(failed.status, failed.code))
      // A refused Connect leaves the table standing, and the button that was
      // pressed is still on the row — so the only recovery worth adding here is
      // the one this page cannot draw twice: signing in again.
      setRecovery(failed.status === UNAUTHORIZED ? 'sign-in' : 'none')
    }
  }

  const connected = params.get(CONNECTED_PARAMETER)
  const calledBack = params.get(ERROR_PARAMETER)

  return (
    <div className="page">
      <main className="mt-6">
        {/* Above the heading, where an "up one level" control belongs, and drawn
            before anything is read — a page that has failed its one request is
            exactly the page an operator most needs to be able to leave. */}
        <div className="mb-4">
          <Link className="btn" href={SETTINGS}>
            Back to Settings
          </Link>
        </div>

        <div className="page-header">
          <h1 className="title-page">Integrations</h1>
        </div>

        <p className="prose mb-5">
          Connect a calendar and Kairos reads the busy time it already holds, so a slot it offers is a slot you
          are actually free for.
        </p>

        {/* The callback's verdict, said once, above the table that now shows
            the consequence. Both carry a dot and words — the success is not a
            green sentence and the failure is not a red one. */}
        {connected !== null && (
          <p className="status mb-5" data-state={LIVE} role="status">
            {label(connected)} is connected.
          </p>
        )}
        {calledBack !== null && (
          <p className="field-error mb-5" role="alert">
            <AlertIcon />
            {calledBackMessage(calledBack)}
          </p>
        )}

        {failure !== '' && phase !== 'loading' && (
          <p className="field-error mb-5" role="alert">
            <AlertIcon />
            <span>
              {failure}
              {/* Both routes into this message — the read that came back 401 and
                  the Connect that did — set the same recovery. */}
              {recovery === 'sign-in' && (
                <>
                  {' '}
                  <a href={SIGN_IN}>Sign in</a> and come back.
                </>
              )}
            </span>
          </p>
        )}

        {/* The action the sentence above just described. Its own block rather
            than a child of the alert: `.field-error` is a centred flex row
            sized for an icon and a sentence, and a 44px control inside it
            stretches the row it is meant to sit under. */}
        {recovery === 'retry' && (
          <div className="mb-5">
            <button
              className="btn"
              type="button"
              data-loading={phase === 'loading' || undefined}
              onClick={() => setAttempt((previous) => previous + 1)}
            >
              Read it again
            </button>
          </div>
        )}

        {/* `locked` and `broken` render no table at all: both have already put
            their one sentence in `failure` above, and column headings over
            nothing would suggest the list came back empty rather than never
            came back. */}
        {phase === 'loading' ? (
          <p className="prose t-sm">Reading what is connected…</p>
        ) : phase !== 'ready' ? null : rows.length === 0 ? (
          // Not an error and not an empty table: a build compiled without a
          // single connector is a working Kairos that simply has nothing to
          // offer here, and saying so beats four empty column headings.
          <div className="empty">
            <p>This build has no calendar providers to connect.</p>
          </div>
        ) : (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th scope="col">Provider</th>
                  <th scope="col">State</th>
                  {/* "Since", not "Connected": below 760px each cell prints its
                      column name beside its value, and a Connected row would
                      then read "Connected / Connected" next to a date. */}
                  <th scope="col">Since</th>
                  <th scope="col">
                    <span className="visually-hidden">Action</span>
                  </th>
                </tr>
              </thead>
              <tbody>
                {rows.map((integration) => {
                  const shown = describe(integration)

                  return (
                    <tr key={integration.provider}>
                      <td data-column="">
                        <span className="cell-primary">{label(integration.provider)}</span>
                        {shown.detail !== '' && <span className="cell-sub">{shown.detail}</span>}
                      </td>
                      <td data-column="State">
                        <span className="status" data-state={shown.live ? LIVE : PAUSED}>
                          {shown.state}
                        </span>
                      </td>
                      <td data-column="Since" className="numeric">
                        {integration.connectedAt === null ? (
                          <span className="muted">—</span>
                        ) : (
                          connectedOn(integration.connectedAt)
                        )}
                      </td>
                      <td data-column="Action">
                        {shown.action !== null && (
                          <button
                            className="btn"
                            type="button"
                            data-loading={pending === integration.provider || undefined}
                            onClick={() => void connect(integration.provider)}
                          >
                            {shown.action}
                          </button>
                        )}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}

        {/* Named rather than silently absent, the same way the manage page names
            a missing Reschedule. Task 4 shipped no fourth route, and a
            Disconnect that calls nothing is worse than none. It needs a DELETE
            that drops the stored credential and revokes the grant — revokes, or
            the token keeps working after Kairos has forgotten it. */}
        {phase === 'ready' && rows.length > 0 && (
          <p className="fine mt-5">
            Disconnecting is not built yet. Revoking Kairos in the provider&apos;s own account settings ends its
            access, and this page then reads Reconnect needed.
          </p>
        )}
      </main>
      {/* No `page-foot` here, deliberately. "Scheduled with Kairos" is the
          invitee's attribution line — it belongs on the booking page and the
          manage page, the surfaces a stranger holding a link reads. */}
    </div>
  )
}
