import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { Router } from 'wouter'
import { memoryLocation } from 'wouter/memory-location'

// Same two replacements locked.test.tsx makes, and for the same reason:
// `ApiError` and `asApiError` stay real, because session.ts decides which
// refusal this is by asking `asApiError` for a status and a code.
vi.mock('../src/lib/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/lib/api')>()),
  request: vi.fn(),
  requestNothing: vi.fn(),
}))

const stylesheet = resolve(process.cwd(), '../backend/src/Kairos.Api/wwwroot/kairos.css')

const ADMIN_BASE = '/admin'
const SIGN_IN_PATH = '/sign-in'
const FORBIDDEN = 403

// The code the admin policy puts in the body of its refusal. Spelled here the
// way the server spells it in Kairos.Api/Security/AdminAuthorization.cs — the
// two are one contract, and a test that generated it from the source could not
// notice either side changing it.
const NOT_AN_OPERATOR = 'not_an_operator'

const HEADING = 'This account is not an operator'

/** A fresh module graph per test: the refusal latches and is never released. */
async function mount(
  path: string,
  answer: (path: string, fail: (status: number, code: string | null) => Error) => Promise<unknown>,
) {
  vi.resetModules()

  const api = await import('../src/lib/api')
  const fail = (status: number, code: string | null) =>
    new api.ApiError(status, code, `The server answered ${status}.`)
  vi.mocked(api.request).mockImplementation(
    (called: string) => answer(called, fail) as Promise<never>,
  )

  const { default: AdminShell } = await import('../src/routes/admin/AdminShell')
  const { hook } = memoryLocation({ path })

  return render(
    <Router hook={hook} base={ADMIN_BASE}>
      <AdminShell />
    </Router>,
  ).container
}

beforeEach(() => {
  vi.clearAllMocks()
})

afterEach(cleanup)

/**
 * The screen the admin boundary made reachable. Before it, an account the
 * identity provider authenticated and Kairos does not admit was impossible; now
 * it is the ordinary state of anyone else with an account at the provider, and
 * without this it read as a server fault.
 */
test('an account the boundary refuses gets its own screen, and no chrome around it', async () => {
  const container = await mount('/admin/links', (_path, fail) =>
    Promise.reject(fail(FORBIDDEN, NOT_AN_OPERATOR)),
  )

  expect(await screen.findByRole('heading', { name: HEADING })).toBeDefined()
  expect(container.querySelector('.admin-shell')).toBeNull()
  expect(container.querySelector('.rail')).toBeNull()
  expect(container.querySelector('.tabbar')).toBeNull()
})

/**
 * The half that makes the code worth carrying at all. Both refusals replace the
 * workbench, and offering "Sign in" to somebody already signed in sends them
 * round the provider and back to this same screen — so the remedy must be the
 * one they can act on, and never the other one.
 */
test('the refused account is not offered a sign-in link', async () => {
  await mount('/admin/links', (path, fail) =>
    path === SIGN_IN_PATH
      ? Promise.resolve({ signInUrl: '/admin/login' })
      : Promise.reject(fail(FORBIDDEN, NOT_AN_OPERATOR)),
  )

  await screen.findByRole('heading', { name: HEADING })

  expect(screen.queryByRole('link', { name: 'Sign in' })).toBeNull()
  expect(screen.queryByRole('heading', { name: 'Sign in to continue' })).toBeNull()
})

/**
 * A 403 that says nothing is not this state, and must not be dressed as it. The
 * status alone cannot tell "you are not an operator" from a refusal Kairos has
 * not written yet, and telling somebody to ask for a group membership they
 * already have is worse than the generic sentence.
 */
test('a 403 with no code leaves the workbench standing', async () => {
  const container = await mount('/admin/links', (_path, fail) =>
    Promise.reject(fail(FORBIDDEN, null)),
  )

  expect(await screen.findByText('The server answered 403.')).toBeDefined()
  expect(container.querySelector('.rail')).not.toBeNull()
  expect(screen.queryByRole('heading', { name: HEADING })).toBeNull()
})

/** The claim that this screen adds no CSS, checked rather than asserted. */
test('every class the refusal screen renders exists in kairos.css', async () => {
  const css = readFileSync(stylesheet, 'utf8')
  const container = await mount('/admin/meetings', (_path, fail) =>
    Promise.reject(fail(FORBIDDEN, NOT_AN_OPERATOR)),
  )

  await screen.findByRole('heading', { name: HEADING })

  const used = new Set(
    [...container.querySelectorAll('[class]')].flatMap((element) => [...element.classList]),
  )

  expect([...used].filter((name) => !css.includes(`.${name}`))).toEqual([])
})
