import { useEffect, useState } from 'react'
import { useSearchParams } from 'wouter'

import AlertIcon from '../components/AlertIcon'
import {
  asApiError,
  connectIntegration,
  listIntegrations,
  PROVIDER_NOT_CONFIGURED,
  type Integration,
} from '../lib/api'
import { localZone, shortDate, zonedDate, zonedTime } from '../lib/time'

// The only two status shapes kairos.css draws, and the Second Channel Rule is
// the whole reason they are shapes: `live` fills the 7px dot, `paused` leaves it
// hollow at the same size. A working connection is the only thing that earns the
// filled one — "not configured", "not connected" and "reconnect needed" are all
// hollow, and the words beside the dot are what tells them apart. Every state
// here survives a monochrome screenshot.
const LIVE = 'live'
const PAUSED = 'paused'

const UNAUTHORIZED = 401

// The two the callback puts on this page's URL. It always redirects here and
// always carries one of them, so the operator learns what happened on the page
// they started from rather than on a blank screen.
const CONNECTED_PARAMETER = 'connected'
const ERROR_PARAMETER = 'error'

/**
 * Whether the page has a list to render.
 *
 * `locked` is its own answer rather than a variety of `broken` because nothing
 * on this page can fix it. See `LOCKED_MESSAGE`.
 */
type Phase = 'loading' | 'ready' | 'locked' | 'broken'

/**
 * What a row says and offers, worked out from the flags alone.
 *
 * No provider name reaches this function. A row describes itself out of
 * `configured` / `connected` / `reconnectRequired`, which is what lets the page
 * render a provider this build has never heard of.
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
 * There is no login UI to send anyone to.
 *
 * `Integrations:AdminToken` is a stopgap the backend documents as one, and Part
 * C replaces it with Pocket ID. Building a token box here would mean inventing
 * an authentication surface that Part C then deletes, and parking a bearer
 * credential in browser storage to do it. So the page does the one honest thing
 * a page with no credential can do: it names the header the deployment is
 * asking for and stops. The operator who set that token is the operator who can
 * unset it, and they are the only person who ever sees this.
 */
const LOCKED_MESSAGE =
  'This Kairos is protected by an admin token, and the page has no way to send one — sign-in arrives with Pocket ID. Until then, clear Integrations:AdminToken from the configuration and restart to use this page.'

/**
 * A provider name as a heading, without knowing any provider.
 *
 * The API sends the lowercase enum name because that is what the connect path
 * takes. Capitalising the first letter is the whole transformation: a lookup
 * table would be a list of vendors in the frontend, which is exactly what Task
 * 4's first endpoint exists to avoid.
 */
function label(provider: string): string {
  return provider.charAt(0).toUpperCase() + provider.slice(1)
}

function describe(integration: Integration): Presentation {
  // Asked first, because it outranks everything below it: with no client
  // credentials on this deployment there is nothing to connect to, and a
  // Connect button here would be a button whose only outcome is
  // `provider_not_configured`. The row explains itself instead.
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

  // Connected and broken. The credential is stored and the provider has stopped
  // honouring it — the grant was revoked from the provider's own account
  // settings, or expired out from under us. It is offered the same button
  // because a fresh consent is the only fix there is; anything vaguer sends the
  // operator into the logs for something one press solves.
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
 * these four sentences are all there is, and each has to name the recovery
 * rather than the fault.
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
  // The provider whose Connect is in flight. It is never cleared on success:
  // the browser is leaving for the consent screen, and a button that stops
  // spinning while the page is still there reads as a press that did nothing.
  const [pending, setPending] = useState('')

  useEffect(() => {
    const controller = new AbortController()

    listIntegrations(controller.signal)
      .then((list) => {
        setRows(list)
        setPhase('ready')
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        const failed = asApiError(error)
        setPhase(failed.status === UNAUTHORIZED ? 'locked' : 'broken')
        setFailure(failed.status === UNAUTHORIZED ? LOCKED_MESSAGE : failed.message)
      })

    return () => controller.abort()
  }, [])

  async function connect(provider: string) {
    setPending(provider)
    setFailure('')

    try {
      const consentUrl = await connectIntegration(provider)
      // Navigated to, never fetched. This is another origin's consent screen —
      // a page a human presses a button on — and the redirect back to
      // /integrations is what finishes the exchange. A fetch here would be
      // refused by CORS and would be the wrong idea even if it were not.
      window.location.assign(consentUrl)
    } catch (error: unknown) {
      const failed = asApiError(error)
      setPending('')
      setFailure(connectMessage(failed.status, failed.code))
    }
  }

  const connected = params.get(CONNECTED_PARAMETER)
  const calledBack = params.get(ERROR_PARAMETER)

  return (
    <div className="page">
      <main className="mt-6">
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
            {failure}
          </p>
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
            a missing Reschedule. Task 4 shipped list, connect and callback and
            no fourth route, and a Disconnect that calls nothing would be worse
            than no Disconnect at all. It needs a DELETE that drops the stored
            credential and revokes the grant with the provider — revokes, or the
            token keeps working after Kairos has forgotten it. */}
        {phase === 'ready' && rows.length > 0 && (
          <p className="fine mt-5">
            Disconnecting is not built yet. Revoking Kairos in the provider&apos;s own account settings ends its
            access, and this page then reads Reconnect needed.
          </p>
        )}
      </main>
      <footer className="fine page-foot">Scheduled with Kairos</footer>
    </div>
  )
}
