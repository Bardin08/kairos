import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { Route, Router } from 'wouter'
import { memoryLocation } from 'wouter/memory-location'

import Integrations from '../src/pages/Integrations'
import {
  ApiError,
  connectIntegration,
  listIntegrations,
  readIntegrationSettings,
  saveIntegrationSettings,
} from '../src/lib/api'
import type { Integration, IntegrationSettings } from '../src/lib/api'

vi.mock('../src/lib/api', async (importOriginal) => ({
  // ApiError and PROVIDER_NOT_CONFIGURED stay real: the page reads a failure
  // with `instanceof` through `asApiError`, and a plain object would send every
  // error down the "carries no status" path while the branches these tests aim
  // at never ran.
  ...(await importOriginal<typeof import('../src/lib/api')>()),
  listIntegrations: vi.fn(),
  connectIntegration: vi.fn(),
  readIntegrationSettings: vi.fn(),
  saveIntegrationSettings: vi.fn(),
}))

const LINKED_ACCOUNT = 'me@example.com'
const LINKED_AT = '2026-08-14T22:29:14Z'

/**
 * The panel's one universal field, as every provider's description carries it.
 * The key is the server's spelling of the reserved switch; this page never reads
 * it, and the fixture carries it only because a real description would.
 */
const ENABLED_FIELD = 'enabled'
const SWITCH_LABEL = 'Use this integration'

/** The disclosure, closed and open. */
const SETTINGS = 'Settings'
const HIDE_SETTINGS = 'Hide settings'

const google: Integration = {
  provider: 'google',
  configured: true,
  connected: false,
  accountLabel: null,
  connectedAt: null,
  reconnectRequired: false,
  enabled: true,
  fault: null,
}

/**
 * A provider that does not exist and never will. The page has to render whatever
 * the array holds, and anything that made this row fail to appear would be a
 * vendor name baked into the frontend — what Task 4's list endpoint exists to
 * prevent.
 */
const invented: Integration = {
  provider: 'fastmail',
  configured: true,
  connected: true,
  accountLabel: 'ops@example.com',
  connectedAt: '2026-08-14T22:29:14Z',
  reconnectRequired: false,
  enabled: true,
  fault: null,
}

/**
 * Connected, working and switched on. The row the disclosure is offered on, and
 * the one every state below is a departure from.
 */
const linked: Integration = {
  ...google,
  connected: true,
  accountLabel: LINKED_ACCOUNT,
  connectedAt: LINKED_AT,
}

/**
 * What the panel is told about a provider that describes nothing of its own: the
 * switch, which the endpoint injects, and nothing else. No provider name in it.
 */
const settings: IntegrationSettings = {
  provider: google.provider,
  fields: [
    {
      key: ENABLED_FIELD,
      kind: 'toggle',
      label: SWITCH_LABEL,
      help: null,
      options: null,
      maxValues: null,
      required: false,
      value: true,
    },
  ],
}

// `searchPath` carries no leading `?` — memoryLocation adds it.
function renderPage(searchPath = '') {
  const { hook, searchHook } = memoryLocation({ path: '/integrations', searchPath })
  // Mounted behind its real route with a search hook, because the callback
  // always lands here carrying ?connected= or ?error= and the page reads them.
  render(
    <Router hook={hook} searchHook={searchHook}>
      <Route path="/integrations" component={Integrations} />
    </Router>,
  )
}

beforeEach(() => {
  vi.mocked(listIntegrations).mockResolvedValue([google])
  vi.mocked(connectIntegration).mockResolvedValue('https://accounts.google.com/o/oauth2/v2/auth?x=1')
  vi.mocked(readIntegrationSettings).mockResolvedValue(settings)
  vi.mocked(saveIntegrationSettings).mockResolvedValue(undefined)
})

// Set by the one test that swaps `window.location` out, so a stub that would
// otherwise outlive it cannot silently break whatever runs next.
let restoreLocation: (() => void) | null = null

afterEach(() => {
  cleanup()
  restoreLocation?.()
  restoreLocation = null
  vi.restoreAllMocks()
  vi.clearAllMocks()
})

// Breaks if the page ever hard-codes a provider: a second row it has no
// knowledge of has to arrive from the backend and render on its own.
test('renders every provider the backend lists, including one it has never heard of', async () => {
  vi.mocked(listIntegrations).mockResolvedValue([google, invented])
  renderPage()

  expect(await screen.findByText('Google')).toBeTruthy()
  expect(screen.getByText('Fastmail')).toBeTruthy()
  expect(screen.getByText('ops@example.com')).toBeTruthy()
})

// Breaks if a working connection stops carrying the filled dot — the Second
// Channel Rule's whole claim is that the state survives without hue.
test('a healthy connection is the only state with a filled dot', async () => {
  vi.mocked(listIntegrations).mockResolvedValue([
    { ...google, connected: true, accountLabel: 'me@example.com', connectedAt: '2026-08-14T22:29:14Z' },
    { ...google, provider: 'fastmail' },
  ])
  renderPage()

  const live = await screen.findByText('Connected')
  expect(live.getAttribute('data-state')).toBe('live')
  expect(screen.getByText('Not connected').getAttribute('data-state')).toBe('paused')
})

// Breaks if the invalid_grant case starts reading as an error rather than as an
// instruction. Reconnect is the only fix, so the button has to be there.
test('reconnectRequired asks for a reconnect and offers the button', async () => {
  vi.mocked(listIntegrations).mockResolvedValue([
    {
      ...google,
      connected: true,
      accountLabel: 'me@example.com',
      connectedAt: '2026-08-14T22:29:14Z',
      reconnectRequired: true,
    },
  ])
  renderPage()

  const state = await screen.findByText('Reconnect needed')
  expect(state.getAttribute('data-state')).toBe('paused')
  expect(screen.getByRole('button', { name: 'Reconnect' })).toBeTruthy()
})

// Breaks if a deployment with no client credentials is offered a Connect button
// whose only possible outcome is provider_not_configured.
test('a provider this deployment cannot configure offers no button and says why', async () => {
  vi.mocked(listIntegrations).mockResolvedValue([{ ...google, configured: false }])
  renderPage()

  expect(await screen.findByText('Not available')).toBeTruthy()
  expect(screen.queryByRole('button')).toBeNull()
  expect(screen.getByText(/no client credentials/)).toBeTruthy()
})

// Breaks if Connect ever fetches the consent URL instead of navigating to it.
// It is another origin's consent screen: fetching it gets a CORS refusal and,
// worse, would mean no human ever sees the screen they have to press a button on.
test('Connect navigates the browser to the consent URL and does not fetch it', async () => {
  // `location.assign` cannot be spied: Location's members are [LegacyUnforgeable]
  // in the spec, so jsdom defines them non-configurable on the instance. The
  // property holding the Location, however, is configurable — so the whole
  // object is swapped and put back afterwards.
  const real = Object.getOwnPropertyDescriptor(window, 'location')
  const assign = vi.fn()
  Object.defineProperty(window, 'location', { configurable: true, value: { assign } })
  if (real !== undefined) restoreLocation = () => Object.defineProperty(window, 'location', real)

  const fetched = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response('{}'))
  renderPage()

  fireEvent.click(await screen.findByRole('button', { name: 'Connect' }))

  await waitFor(() => {
    expect(assign).toHaveBeenCalledWith('https://accounts.google.com/o/oauth2/v2/auth?x=1')
  })
  expect(connectIntegration).toHaveBeenCalledWith('google')
  expect(fetched).not.toHaveBeenCalled()
})

// Breaks if the callback's verdict stops reaching the operator. The redirect is
// the only thing that ever reports what happened on the consent screen.
test('the callback reports success and refusal from the query string', async () => {
  renderPage('connected=google')
  expect(await screen.findByText('Google is connected.')).toBeTruthy()

  cleanup()

  renderPage('error=consent_denied')
  expect(await screen.findByText(/closed without granting access/)).toBeTruthy()
})

// Breaks if the page loses its only edge back into the workbench, and breaks if
// the invitee's attribution line returns. This page renders outside the admin
// shell — no rail, no tab bar — so the link it draws itself is the whole of the
// first, and the operator is not the reader the second was written for.
test('offers the way back to Settings and the legal links, but no invitee footer', async () => {
  renderPage()

  const back = await screen.findByRole('link', { name: 'Back to Settings' })
  expect(back.getAttribute('href')).toBe('/admin/settings')
  expect(screen.queryByText('Scheduled with Kairos')).toBeNull()
  expect(screen.getByRole('link', { name: 'Privacy' }).getAttribute('href')).toBe('/privacy')
})

// Breaks if a credential this deployment cannot decrypt goes back to being a
// sentence with nothing to press. Reconnecting is the fix, so the row reporting
// the fault carries the button that performs it — even where the deployment holds
// no client credentials, because that is exactly the instance the operator is on.
test('a credential that will not decrypt offers the reconnect it asks for', async () => {
  vi.mocked(listIntegrations).mockResolvedValue([
    {
      ...google,
      configured: false,
      connected: true,
      accountLabel: 'me@example.com',
      connectedAt: '2026-08-14T22:29:14Z',
      fault: 'credential_unreadable',
    },
  ])
  renderPage()

  const state = await screen.findByText('Reconnect needed')
  expect(state.getAttribute('data-state')).toBe('paused')
  expect(screen.getByText(/different Security:SecretKey/)).toBeTruthy()
  // The prerequisite is named as well as the action, so the button is not a
  // surprise when it comes back with provider_not_configured.
  expect(screen.getByText(/no client credentials/)).toBeTruthy()
  expect(screen.getByRole('button', { name: 'Connect again' })).toBeTruthy()
})

// Breaks if a failure the page cannot act on goes back to being a bare
// sentence. Reading again is the only move left, so it is offered as a control
// and said in words rather than left for the operator to guess at.
test('a failure with no remedy but a retry says so and offers the retry', async () => {
  vi.mocked(listIntegrations).mockRejectedValueOnce(new ApiError(500, null, 'The server answered 500.'))
  renderPage()

  expect(await screen.findByText(/Nothing on this page can put that right/)).toBeTruthy()
  // Not a Connect: nothing about a dead server is fixed by opening a consent
  // screen, and a button whose press cannot help is worse than none.
  expect(screen.queryByRole('button', { name: /Connect/ })).toBeNull()

  fireEvent.click(screen.getByRole('button', { name: 'Read it again' }))

  expect(await screen.findByText('Google')).toBeTruthy()
  expect(listIntegrations).toHaveBeenCalledTimes(2)
})

// Breaks if the way out disappears from the screens that need it most. The page
// renders outside the admin shell, so on a failure this link is the only thing
// on it that leads anywhere.
test('the way back to Settings survives every failure state', async () => {
  vi.mocked(listIntegrations).mockRejectedValue(new ApiError(500, null, 'The server answered 500.'))
  renderPage()

  expect(await screen.findByRole('link', { name: 'Back to Settings' })).toBeTruthy()

  cleanup()

  vi.mocked(listIntegrations).mockRejectedValue(new ApiError(401, null, 'Unauthorized'))
  renderPage()

  expect(await screen.findByRole('link', { name: 'Back to Settings' })).toBeTruthy()
})

// Breaks if a 401 starts rendering as "could not reach the server", and breaks
// if the recovery goes stale again: this sentence used to name a configuration
// key Task 1 had already deleted, so it told the operator to unset something
// that no longer existed. Signing in is the fix, and the link is the assertion.
test('a 401 offers the sign-in instead of an empty table', async () => {
  vi.mocked(listIntegrations).mockRejectedValue(new ApiError(401, null, 'Unauthorized'))
  renderPage()

  const signIn = await screen.findByRole('link', { name: 'Sign in' })

  // A plain anchor, not a router link: /admin/login is a server route under a
  // path the single page nests on, and wouter would swallow the navigation.
  expect(signIn.getAttribute('href')).toBe('/admin/login')
  expect(screen.queryByRole('table')).toBeNull()
})

// Breaks if a connection the operator switched off goes back to reading
// Connected. Nothing is being read or written on that row, and a page that says
// otherwise is the disagreement between two screens this build exists to end.
test('a connection that is switched off reads Paused and keeps the hollow dot', async () => {
  vi.mocked(listIntegrations).mockResolvedValue([{ ...linked, enabled: false }])
  renderPage()

  const state = await screen.findByText('Paused')
  expect(state.getAttribute('data-state')).toBe('paused')
  expect(screen.getByText(/connection is kept/)).toBeTruthy()
})

// Breaks if the switch starts outranking the fault. A paused connection that is
// also broken has something a button can fix and the switch is not it, so the
// row has to keep saying so — and keep the button.
test('a paused connection that is also broken reports the fault rather than the switch', async () => {
  vi.mocked(listIntegrations).mockResolvedValue([
    { ...linked, enabled: false, reconnectRequired: true },
  ])
  renderPage()

  expect(await screen.findByText('Reconnect needed')).toBeTruthy()
  expect(screen.queryByText('Paused')).toBeNull()
  expect(screen.getByRole('button', { name: 'Reconnect' })).toBeTruthy()
})

// Breaks if the disclosure appears on a row with nothing behind it: the switch
// is a column on the stored credential, so a provider nobody connected has
// nothing to configure and the read would come back 404. A broken grant is the
// case worth pinning — it keeps its Connect button and gains the disclosure,
// because the switch is still worth reaching while the grant is being fixed.
test('settings are offered on every row holding a credential and on no row without one', async () => {
  vi.mocked(listIntegrations).mockResolvedValue([
    linked,
    { ...linked, provider: 'fastmail', reconnectRequired: true },
    google,
    { ...google, provider: 'outlook', configured: false },
  ])
  renderPage()

  const controls = await screen.findAllByRole('button', { name: SETTINGS })
  expect(controls).toHaveLength(2)
  expect(screen.getByRole('button', { name: 'Reconnect' })).toBeTruthy()
})

// Breaks if the panel stops being about the row it opened from, and breaks if
// the disclosure stops closing: two open panels are two Save buttons with no way
// to tell which one a press belongs to.
test('expanding a row reads that provider settings and folds them away again', async () => {
  vi.mocked(listIntegrations).mockResolvedValue([linked])
  renderPage()

  fireEvent.click(await screen.findByRole('button', { name: SETTINGS }))

  expect(await screen.findByLabelText(SWITCH_LABEL)).toBeTruthy()
  expect(readIntegrationSettings).toHaveBeenCalledWith(google.provider)

  fireEvent.click(screen.getByRole('button', { name: HIDE_SETTINGS }))

  expect(screen.queryByLabelText(SWITCH_LABEL)).toBeNull()
})

// Breaks if a settings read that fails takes the table with it. The row is the
// only thing that could not be read, and the listing beside it is still true —
// the same isolation the listing already gives a credential it cannot decrypt.
test('a settings read that fails says so inside the row and leaves the table standing', async () => {
  vi.mocked(listIntegrations).mockResolvedValue([linked])
  vi.mocked(readIntegrationSettings).mockRejectedValue(
    new ApiError(500, null, 'The server answered 500.'),
  )
  renderPage()

  fireEvent.click(await screen.findByRole('button', { name: SETTINGS }))

  const said = await screen.findByText(/Nothing on this page can put that right/)
  expect(said.closest('td')).not.toBeNull()
  expect(screen.getByRole('table')).toBeTruthy()
  expect(screen.getByText('Connected')).toBeTruthy()

  cleanup()

  vi.mocked(readIntegrationSettings).mockRejectedValue(new ApiError(401, null, 'Unauthorized'))
  renderPage()

  fireEvent.click(await screen.findByRole('button', { name: SETTINGS }))

  expect(await screen.findByText(/session has expired/)).toBeTruthy()
})

// Breaks if a save refreshes the panel and not the listing. The switch is the
// row's own state, so a table left unread says Connected beside a panel that has
// just been switched off.
test('a saved setting refreshes the listing under the panel, not only the panel', async () => {
  vi.mocked(listIntegrations).mockResolvedValue([linked])
  renderPage()

  fireEvent.click(await screen.findByRole('button', { name: SETTINGS }))
  expect(await screen.findByLabelText(SWITCH_LABEL)).toBeTruthy()

  fireEvent.click(screen.getByRole('button', { name: 'Save' }))

  await waitFor(() => expect(listIntegrations).toHaveBeenCalledTimes(2))
  expect(saveIntegrationSettings).toHaveBeenCalled()
  expect(screen.queryByLabelText(SWITCH_LABEL)).toBeNull()
})
