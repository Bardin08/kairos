import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { Router } from 'wouter'
import { memoryLocation } from 'wouter/memory-location'

// `request` and `requestNothing` are the only two things replaced. `ApiError`
// and `asApiError` stay real, because session.ts decides whether to lock by
// asking `asApiError` for a status and that answer is `instanceof`-based — a
// stubbed one would make the guard pass on a shape the running app never sees.
vi.mock('../src/lib/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/lib/api')>()),
  request: vi.fn(),
  requestNothing: vi.fn(),
}))

const stylesheet = resolve(process.cwd(), '../backend/src/Kairos.Api/wwwroot/kairos.css')

const ADMIN_BASE = '/admin'
const SIGN_IN_PATH = '/sign-in'
const UNAUTHORIZED = 401

const settings = {
  timeZone: 'UTC',
  signedInAs: 'Vlad Bardin',
  marginMinutes: 15,
  minNoticeMinutes: 120,
  dailyCap: 4,
  startIncrementMinutes: 30,
  overrides: { marginMinutes: 0, minNoticeMinutes: 0, dailyCap: 0, startIncrementMinutes: 0 },
}

/**
 * A fresh module graph per test, and the reason is session.ts's whole design:
 * the lock latches and is never released, because the way out of it is a full
 * browser navigation to the provider and back — a new document. Kept across
 * tests, the first 401 would lock every test after it.
 *
 * `answer` is the whole server: it receives the path `request` was called with
 * and either resolves or rejects. The ApiError it rejects with comes from the
 * freshly imported module so it is the same class `asApiError` will test
 * against.
 */
async function mount(
  path: string,
  answer: (path: string, fail: (status: number) => Error) => Promise<unknown>,
) {
  vi.resetModules()

  const api = await import('../src/lib/api')
  const fail = (status: number) => new api.ApiError(status, null, `The server answered ${status}.`)
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

/** Everything the workbench draws that must not exist without a session. */
function chrome(container: HTMLElement) {
  return {
    shell: container.querySelector('.admin-shell'),
    rail: container.querySelector('.rail'),
    tabbar: container.querySelector('.tabbar'),
    buttons: container.querySelectorAll('button').length,
    empties: container.querySelectorAll('.empty').length,
    pageHeaders: container.querySelectorAll('.page-header').length,
  }
}

beforeEach(() => {
  vi.clearAllMocks()
})

afterEach(cleanup)

/**
 * The bug this whole part exists for, from a real screenshot.
 *
 * With an expired session /admin/availability rendered its heading, its
 * pattern-name field, its New pattern button and "No patterns yet. A pattern is
 * the weekly shape a link books against…" with a small banner above it. Every
 * word of that empty state was a lie: the page did not learn there were no
 * patterns, it was refused permission to ask, and an operator with fifty of them
 * read it as fifty gone.
 *
 * Absent from the DOM rather than hidden. A rail that is display:none is still
 * in the tab order of nothing and still in the document a screen reader walks,
 * and "hidden" is a rendering decision that a media query can undo.
 */
test('a refused read replaces the whole workbench, chrome and all', async () => {
  const container = await mount('/admin/availability', (path, fail) =>
    path === SIGN_IN_PATH
      ? Promise.resolve({ signInUrl: '/admin/login' })
      : Promise.reject(fail(UNAUTHORIZED)),
  )

  expect(await screen.findByRole('link', { name: 'Sign in' })).toBeDefined()
  expect(chrome(container)).toEqual({
    shell: null,
    rail: null,
    tabbar: null,
    buttons: 0,
    empties: 0,
    pageHeaders: 0,
  })
  // The sentence the screenshot showed, and the one thing that must never be on
  // this screen: a claim about data nobody was allowed to read.
  expect(screen.queryByText(/No patterns yet/)).toBeNull()
  expect(screen.queryByRole('heading', { name: 'Availability' })).toBeNull()
})

/**
 * The URL is deployment configuration, not a compile-time constant.
 *
 * One bundle is built and deployed everywhere, and an operator who wants the
 * browser sent through a portal or a provider's own page first sets
 * Authentication__SignInUrl and restarts. A default baked in here would make
 * that setting unreachable from the one screen that needs it.
 */
test('the sign-in link is whatever the server said, not a constant in the bundle', async () => {
  const elsewhere = 'https://id.example.test/authorize/kairos'

  await mount('/admin/links', (path, fail) =>
    path === SIGN_IN_PATH
      ? Promise.resolve({ signInUrl: elsewhere })
      : Promise.reject(fail(UNAUTHORIZED)),
  )

  const link = await screen.findByRole('link', { name: 'Sign in' })

  expect(link.getAttribute('href')).toBe(elsewhere)
})

/**
 * The guard is on the data layer, not on the shell's own read.
 *
 * Here the shell's settings call succeeds — the rail would have drawn — and it
 * is the Links page's own list call that is refused. Wired to the shell alone,
 * this is exactly the case that would have rendered a rail, a New link button
 * and an empty list, which is the other half of the screenshot.
 */
test('a page whose own call is refused locks the shell that mounted it', async () => {
  const container = await mount('/admin/links', (path, fail) => {
    if (path === SIGN_IN_PATH) return Promise.resolve({ signInUrl: '/admin/login' })
    if (path === '/admin/settings') return Promise.resolve(settings)
    return Promise.reject(fail(UNAUTHORIZED))
  })

  expect(await screen.findByRole('link', { name: 'Sign in' })).toBeDefined()
  expect(chrome(container).rail).toBeNull()
  expect(screen.queryByRole('button', { name: /New link/ })).toBeNull()
})

/**
 * A failure that is not a 401 is not a locked session, and must not be dressed
 * as one. The shell stays, the page reports its own sentence, and signing in
 * would fix nothing.
 */
test('a failure that is not a refusal leaves the workbench standing', async () => {
  const container = await mount('/admin/links', (path, fail) =>
    path === '/admin/settings'
      ? Promise.resolve(settings)
      : Promise.reject(fail(503)),
  )

  expect(await screen.findByText('The server answered 503.')).toBeDefined()
  expect(container.querySelector('.rail')).not.toBeNull()
  expect(screen.queryByRole('link', { name: 'Sign in' })).toBeNull()
})

/**
 * The claim that this part adds no CSS, checked rather than asserted. A class
 * invented here would style nothing and fail silently — the card simply would
 * not be a card, at a width no unit test can see.
 */
test('every class the locked screen renders exists in kairos.css', async () => {
  const css = readFileSync(stylesheet, 'utf8')
  const container = await mount('/admin/meetings', (path, fail) =>
    path === SIGN_IN_PATH
      ? Promise.resolve({ signInUrl: '/admin/login' })
      : Promise.reject(fail(UNAUTHORIZED)),
  )

  await screen.findByRole('link', { name: 'Sign in' })

  const used = new Set(
    [...container.querySelectorAll('[class]')].flatMap((element) => [...element.classList]),
  )

  expect([...used].filter((name) => !css.includes(`.${name}`))).toEqual([])
})
