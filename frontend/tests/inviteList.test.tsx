import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'

// ApiError comes from the real module and is never mocked: `Failure` reads a
// rejection through `asApiError`, which recognises it with `instanceof`. A plain
// object would take the "carries no status" path and the server's own sentence
// would be replaced with "could not reach the server".
import { ApiError } from '../src/lib/api'
import {
  createInvite,
  listInvites,
  regenerateInvite,
  revokeInvite,
  type AdminInvite,
} from '../src/routes/admin/adminInvites'
import InviteList from '../src/routes/admin/InviteList'

vi.mock('../src/routes/admin/adminInvites', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/adminInvites')>()),
  listInvites: vi.fn(),
  createInvite: vi.fn(),
  revokeInvite: vi.fn(),
  regenerateInvite: vi.fn(),
}))

const LINK_ID = '018f3a00-0000-7000-8000-000000000001'
const ADDRESSED_INVITE_ID = '018f3a00-0000-7000-8000-000000000002'
const UNADDRESSED_INVITE_ID = '018f3a00-0000-7000-8000-000000000003'
const REPLACEMENT_INVITE_ID = '018f3a00-0000-7000-8000-000000000004'

const LINK_SLUG = 'consulting'
const INVITE_TOKEN = '0f1e2d3c4b5a69788796a5b4c3d2e1f0'
const REPLACEMENT_TOKEN = 'a1b2c3d4e5f60718293a4b5c6d7e8f90'

/**
 * What the server mints, and the only place a URL ever exists. The booking route
 * is `/book/:slug` — `App.tsx` claims it and the backend builds it — so there is
 * no `/b/` anywhere in this feature.
 */
const INVITE_URL = `https://kairos.test/book/${LINK_SLUG}?i=${INVITE_TOKEN}`
const REPLACEMENT_URL = `https://kairos.test/book/${LINK_SLUG}?i=${REPLACEMENT_TOKEN}`

/** The query key a token rides on, and therefore the substring no row may carry. */
const TOKEN_QUERY = '?i='

const ADDRESSEE_NAME = 'Ada Lovelace'
const ADDRESSEE_EMAIL = 'ada@example.com'
const ISSUED_AT = '2026-08-27T10:00:00Z'

/** What a row with an empty prefill says instead of leaving the cell blank. */
const UNADDRESSED_TITLE = 'Nobody in particular'
const UNADDRESSED_DETAIL = 'A working single-use link that fills nothing in.'

const REVOKED_WORD = 'Revoked'
const GENERATE = 'Generate link'
const REVOKE = 'Revoke'
const REGENERATE = 'Regenerate'
const DEADLINE_FIELD = 'Deadline'
const NAME_FIELD = 'Their name'

const SHOWN_ONCE = /shown once and cannot be read back/

const HTTP_BAD_REQUEST = 400
const PREFILL_INVALID = 'prefill_invalid'
/** The server's own words. This is the one prefill refusal this form can provoke. */
const PREFILL_INVALID_MESSAGE =
  "'name' is longer than the 200 characters the booking form accepts."

const HOURS_PER_DAY = 24
const MS_PER_HOUR = 3_600_000
const A_DAY_IN_MS = HOURS_PER_DAY * MS_PER_HOUR

const ADDRESSED: AdminInvite = {
  id: ADDRESSED_INVITE_ID,
  state: 'open',
  prefill: { name: ADDRESSEE_NAME, email: ADDRESSEE_EMAIL },
  expiresAt: null,
  createdAt: ISSUED_AT,
}

const UNADDRESSED: AdminInvite = {
  id: UNADDRESSED_INVITE_ID,
  state: 'open',
  prefill: {},
  expiresAt: null,
  createdAt: ISSUED_AT,
}

const REVOKED: AdminInvite = { ...ADDRESSED, state: 'revoked' }

function show() {
  return render(<InviteList linkId={LINK_ID} />)
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(listInvites).mockResolvedValue([])
})

afterEach(cleanup)

test('the URL is shown once, at creation, in a field the operator can copy', async () => {
  vi.mocked(createInvite).mockResolvedValue({ id: ADDRESSED_INVITE_ID, url: INVITE_URL })

  show()

  fireEvent.click(await screen.findByRole('button', { name: GENERATE }))

  expect(await screen.findByDisplayValue(INVITE_URL)).toBeTruthy()
  expect(screen.getByText(SHOWN_ONCE)).toBeTruthy()
  // Nothing typed and no deadline chosen is a valid invite: a single-use URL
  // with nothing seeded.
  expect(createInvite).toHaveBeenCalledWith(LINK_ID, { prefill: {}, expiresAt: null })
})

/**
 * The announcement and the focus a server-rendered page got for free. A field
 * that mounts with its text already in it is not reliably announced, so the URL
 * is put under the cursor instead — and `onFocus` selects it.
 */
test('the URL field takes focus the moment it appears', async () => {
  vi.mocked(createInvite).mockResolvedValue({ id: ADDRESSED_INVITE_ID, url: INVITE_URL })

  show()

  fireEvent.click(await screen.findByRole('button', { name: GENERATE }))

  const field = await screen.findByDisplayValue(INVITE_URL)

  expect(document.activeElement).toBe(field)
})

test('a deadline preset travels as an instant, because the wire carries a moment', async () => {
  vi.mocked(createInvite).mockResolvedValue({ id: ADDRESSED_INVITE_ID, url: INVITE_URL })

  show()

  fireEvent.change(await screen.findByLabelText(DEADLINE_FIELD), {
    target: { value: String(HOURS_PER_DAY) },
  })

  const before = Date.now()
  fireEvent.click(screen.getByRole('button', { name: GENERATE }))
  await waitFor(() => expect(createInvite).toHaveBeenCalled())
  const after = Date.now()

  const sent = vi.mocked(createInvite).mock.calls[0][1].expiresAt
  const deadline = Date.parse(sent ?? '')

  expect(deadline).toBeGreaterThanOrEqual(before + A_DAY_IN_MS)
  expect(deadline).toBeLessThanOrEqual(after + A_DAY_IN_MS)
})

test('an invite with nothing filled in still reads as a working link, not a blank cell', async () => {
  vi.mocked(listInvites).mockResolvedValue([UNADDRESSED])

  show()

  expect(await screen.findByText(UNADDRESSED_TITLE)).toBeTruthy()
  expect(screen.getByText(UNADDRESSED_DETAIL)).toBeTruthy()
})

test('a prefilled invite names who it is for', async () => {
  vi.mocked(listInvites).mockResolvedValue([ADDRESSED])

  show()

  expect(await screen.findByText(ADDRESSEE_NAME)).toBeTruthy()
  expect(screen.getByText(ADDRESSEE_EMAIL)).toBeTruthy()
})

/**
 * The security property, and the reason `InviteView` has no field for a URL: the
 * server keeps a SHA-256 of the token and nothing else, so a list that appeared
 * to show one would be showing something it invented.
 */
test('no row in the list carries a URL, because there is none left to show', async () => {
  vi.mocked(listInvites).mockResolvedValue([ADDRESSED, UNADDRESSED])

  const { container } = show()

  await screen.findByText(ADDRESSEE_NAME)

  expect(container.textContent).not.toContain(TOKEN_QUERY)
  expect(container.querySelector('input[readonly]')).toBeNull()
  expect(screen.queryByDisplayValue(INVITE_URL)).toBeNull()
})

test('revoke takes the invite back, and the refreshed row reads revoked', async () => {
  vi.mocked(listInvites).mockResolvedValueOnce([ADDRESSED]).mockResolvedValueOnce([REVOKED])
  vi.mocked(revokeInvite).mockResolvedValue(undefined)

  show()

  fireEvent.click(await screen.findByRole('button', { name: REVOKE }))

  expect(await screen.findByText(REVOKED_WORD)).toBeTruthy()
  expect(revokeInvite).toHaveBeenCalledWith(LINK_ID, ADDRESSED_INVITE_ID)
  // Offered on an open invite only: regenerate is refused 409 `invite_not_open`
  // on the other three, and revoke is idempotent, so there it would answer 204
  // having written nothing.
  expect(screen.queryByRole('button', { name: REVOKE })).toBeNull()
  expect(screen.queryByRole('button', { name: REGENERATE })).toBeNull()
})

/**
 * The line this pins — `issued?.id === invite.id` — is the only thing standing
 * between an operator and losing a URL nobody can reissue. Revoking the invite
 * on show kills its URL and the field has to go; revoking any other invite must
 * not touch it, because an operator who has not copied it yet would lose it for
 * good. Two tests because it is one decision with two wrong answers.
 */
test('revoking a different invite leaves the URL on screen', async () => {
  // Newest first, which is how the server orders them: the invite generated
  // below is the unaddressed one, and the addressed row is the older neighbour.
  vi.mocked(listInvites).mockResolvedValue([UNADDRESSED, ADDRESSED])
  vi.mocked(createInvite).mockResolvedValue({ id: UNADDRESSED_INVITE_ID, url: INVITE_URL })
  vi.mocked(revokeInvite).mockResolvedValue(undefined)

  show()

  fireEvent.click(await screen.findByRole('button', { name: GENERATE }))
  expect(await screen.findByDisplayValue(INVITE_URL)).toBeTruthy()

  const neighbour = screen.getByText(ADDRESSEE_NAME).closest('tr')
  fireEvent.click(within(neighbour as HTMLTableRowElement).getByRole('button', { name: REVOKE }))

  await waitFor(() => expect(revokeInvite).toHaveBeenCalledWith(LINK_ID, ADDRESSED_INVITE_ID))
  expect(screen.getByDisplayValue(INVITE_URL)).toBeTruthy()
})

test('revoking the invite on show takes its URL away, because that URL is dead', async () => {
  vi.mocked(listInvites)
    .mockResolvedValueOnce([ADDRESSED])
    .mockResolvedValueOnce([ADDRESSED])
    .mockResolvedValue([REVOKED])
  vi.mocked(createInvite).mockResolvedValue({ id: ADDRESSED_INVITE_ID, url: INVITE_URL })
  vi.mocked(revokeInvite).mockResolvedValue(undefined)

  show()

  fireEvent.click(await screen.findByRole('button', { name: GENERATE }))
  expect(await screen.findByDisplayValue(INVITE_URL)).toBeTruthy()

  fireEvent.click(screen.getByRole('button', { name: REVOKE }))

  await waitFor(() => expect(screen.queryByDisplayValue(INVITE_URL)).toBeNull())
})

test('a refused generate says why, in the words the server used', async () => {
  vi.mocked(createInvite).mockRejectedValue(
    new ApiError(HTTP_BAD_REQUEST, PREFILL_INVALID, PREFILL_INVALID_MESSAGE),
  )

  show()

  fireEvent.change(await screen.findByLabelText(NAME_FIELD), {
    target: { value: ADDRESSEE_NAME },
  })
  fireEvent.click(screen.getByRole('button', { name: GENERATE }))

  expect(await screen.findByText(PREFILL_INVALID_MESSAGE)).toBeTruthy()
  expect(screen.queryByDisplayValue(INVITE_URL)).toBeNull()
})

/**
 * Regenerate answers "the URL was lost", and the replacement is a different
 * invite with a different id — so the field has to show the new URL rather than
 * keep the one that no longer works.
 */
test('regenerate replaces the URL on show with the one it just minted', async () => {
  vi.mocked(listInvites).mockResolvedValue([ADDRESSED])
  vi.mocked(createInvite).mockResolvedValue({ id: ADDRESSED_INVITE_ID, url: INVITE_URL })
  vi.mocked(regenerateInvite).mockResolvedValue({
    id: REPLACEMENT_INVITE_ID,
    url: REPLACEMENT_URL,
  })

  show()

  fireEvent.click(await screen.findByRole('button', { name: GENERATE }))
  expect(await screen.findByDisplayValue(INVITE_URL)).toBeTruthy()

  fireEvent.click(screen.getByRole('button', { name: REGENERATE }))

  expect(await screen.findByDisplayValue(REPLACEMENT_URL)).toBeTruthy()
  expect(screen.queryByDisplayValue(INVITE_URL)).toBeNull()
  expect(regenerateInvite).toHaveBeenCalledWith(LINK_ID, ADDRESSED_INVITE_ID)
})
