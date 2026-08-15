import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { Route, Router } from 'wouter'
import { memoryLocation } from 'wouter/memory-location'

import Integrations from '../src/pages/Integrations'
import { ApiError, connectIntegration, listIntegrations } from '../src/lib/api'
import type { Integration } from '../src/lib/api'

vi.mock('../src/lib/api', async (importOriginal) => ({
  // ApiError and PROVIDER_NOT_CONFIGURED stay real: the page reads a failure
  // with `instanceof` through `asApiError`, and a plain object would send every
  // error down the "carries no status" path while the branches these tests aim
  // at never ran.
  ...(await importOriginal<typeof import('../src/lib/api')>()),
  listIntegrations: vi.fn(),
  connectIntegration: vi.fn(),
}))

const google: Integration = {
  provider: 'google',
  configured: true,
  connected: false,
  accountLabel: null,
  connectedAt: null,
  reconnectRequired: false,
}

/**
 * A provider that does not exist and never will.
 *
 * It is the point of the first test: the page has to render whatever the array
 * holds. Anything that made this row fail to appear would be a vendor name
 * baked into the frontend, which is what Task 4's list endpoint exists to
 * prevent.
 */
const invented: Integration = {
  provider: 'fastmail',
  configured: true,
  connected: true,
  accountLabel: 'ops@example.com',
  connectedAt: '2026-08-14T22:29:14Z',
  reconnectRequired: false,
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
