import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'

// ApiError comes from the real module and is never mocked: `Failure` reads a
// rejection through `asApiError`, which recognises it with `instanceof`. A plain
// object would take the "carries no status" path and the server's own sentence
// would be replaced with "could not reach the server".
import { ApiError } from '../src/lib/api'
import {
  createInvite,
  listAllInvites,
  regenerateInvite,
  revokeInvite,
  type AdminInvite,
} from '../src/routes/admin/adminInvites'
import { listLinks, type AdminLink, type LinksPage } from '../src/routes/admin/adminLinks'
import Invites from '../src/routes/admin/Invites'

vi.mock('../src/routes/admin/adminInvites', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/adminInvites')>()),
  listAllInvites: vi.fn(),
  createInvite: vi.fn(),
  revokeInvite: vi.fn(),
  regenerateInvite: vi.fn(),
}))

// The page reads the links too, for the picker Generate sends its invite
// against. Without this the real client fires a fetch at the links route and the
// page settles on "no links yet" — which is a passing render of the wrong page.
vi.mock('../src/routes/admin/adminLinks', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/routes/admin/adminLinks')>()),
  listLinks: vi.fn(),
}))

const LINK_ID = '018f3a00-0000-7000-8000-000000000001'
const OTHER_LINK_ID = '018f3a00-0000-7000-8000-000000000011'
const ARCHIVED_LINK_ID = '018f3a00-0000-7000-8000-000000000012'
const ADDRESSED_INVITE_ID = '018f3a00-0000-7000-8000-000000000002'
const UNADDRESSED_INVITE_ID = '018f3a00-0000-7000-8000-000000000003'
const REPLACEMENT_INVITE_ID = '018f3a00-0000-7000-8000-000000000004'
const OTHER_LINK_INVITE_ID = '018f3a00-0000-7000-8000-000000000005'
const SPENT_INVITE_ID = '018f3a00-0000-7000-8000-000000000006'
const EXPIRED_INVITE_ID = '018f3a00-0000-7000-8000-000000000007'
const LAPSED_INVITE_ID = '018f3a00-0000-7000-8000-000000000008'
const ARCHIVED_LINK_INVITE_ID = '018f3a00-0000-7000-8000-000000000009'

const LINK_SLUG = 'consulting'
const LINK_NAME = 'Consulting call'
const OTHER_LINK_SLUG = 'mentoring'
const OTHER_LINK_NAME = 'Mentoring hour'
const ARCHIVED_LINK_SLUG = 'retired-call'
const ARCHIVED_LINK_NAME = 'Retired call'

const PATTERN_ID = '018f3a00-0000-7000-8000-0000000000a1'
const PATTERN_NAME = 'Weekdays'

const SHORT_CALL = 25
const LONG_CALL = 50
const GLOBAL_MARGIN = 15
const GLOBAL_NOTICE = 120
const GLOBAL_CAP = 4
const GLOBAL_INCREMENT = 30

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
const OTHER_ADDRESSEE_NAME = 'Grace Hopper'
const ARCHIVED_ADDRESSEE_NAME = 'Katherine Johnson'
const ISSUED_AT = '2026-08-27T10:00:00Z'

/** What a row with an empty prefill says instead of leaving the cell blank. */
const UNADDRESSED_TITLE = 'Nobody in particular'
const UNADDRESSED_DETAIL = 'A working single-use link that fills nothing in.'

const REVOKED_WORD = 'Revoked'
const USED_WORD = 'Used'
const EXPIRED_WORD = 'Expired'
const GENERATE = 'Generate link'
const REVOKE = 'Revoke'
const REGENERATE = 'Regenerate'
const NEW_INVITE = 'New personal link'
const CANCEL = 'Cancel'
const EXPANDED = 'aria-expanded'
const PRIMARY = 'btn-primary'
const GHOST = 'btn-ghost'

/** Same file the running app loads, and the authority on which class names exist. */
const STYLESHEET = resolve(process.cwd(), '../backend/src/Kairos.Api/wwwroot/kairos.css')
const PAGE_HEADER = 'page-header'
const PANEL_ID = 'invite-panel'
const OPEN_FILTER = 'Open'
const ALL_FILTER = 'All'
const DEADLINE_FIELD = 'Deadline'
const NAME_FIELD = 'Their name'
const EMAIL_FIELD = 'Their email'
const LINK_FIELD = 'Link'
const CHANGE = 'Change'
const NO_DEADLINE_WORD = 'no deadline'

const SHOWN_ONCE = /shown once and cannot be read back/
/**
 * The recovery the page names when there is no live link to invite anyone to,
 * whether that is no links at all or every link archived. One sentence, so this
 * matches on the half that is true either way.
 */
const NO_LIVE_LINK = /make one, or restore one you have archived/
/** Nothing has been issued on any link — a state no filter can change. */
const NO_INVITES = /No personal links yet/
/** Invites exist, but the filter is holding them back. */
const NONE_OPEN = /No personal link is still open/

const HTTP_BAD_REQUEST = 400
const HTTP_SERVER_ERROR = 500
const LINKS_UNREADABLE = 'server_error'
/** What the operator is told when the read that fills the picker never lands. */
const LINKS_UNREADABLE_MESSAGE = 'Something went wrong at our end.'
const PREFILL_INVALID = 'prefill_invalid'
/** The server's own words. This is the one prefill refusal this form can provoke. */
const PREFILL_INVALID_MESSAGE =
  "'name' is longer than the 200 characters the booking form accepts."

const HOURS_PER_DAY = 24
const MS_PER_HOUR = 3_600_000
const A_DAY_IN_MS = HOURS_PER_DAY * MS_PER_HOUR

const LINK: AdminLink = {
  id: LINK_ID,
  slug: LINK_SLUG,
  name: LINK_NAME,
  description: 'A chat about your system.',
  patternId: PATTERN_ID,
  patternName: PATTERN_NAME,
  active: true,
  durations: [SHORT_CALL, LONG_CALL],
  marginMinutes: null,
  minNoticeMinutes: null,
  dailyCap: null,
  startIncrementMinutes: null,
}

const OTHER_LINK: AdminLink = {
  ...LINK,
  id: OTHER_LINK_ID,
  slug: OTHER_LINK_SLUG,
  name: OTHER_LINK_NAME,
}

/** Off, not gone: its booking page 404s, so an invite on it would be dead on arrival. */
const ARCHIVED_LINK: AdminLink = {
  ...LINK,
  id: ARCHIVED_LINK_ID,
  slug: ARCHIVED_LINK_SLUG,
  name: ARCHIVED_LINK_NAME,
  active: false,
}

/** What `listLinks` answers: a page, not a bare array. */
function linksPage(links: AdminLink[]): LinksPage {
  return {
    links,
    patterns: [{ id: PATTERN_ID, name: PATTERN_NAME }],
    globals: {
      marginMinutes: GLOBAL_MARGIN,
      minNoticeMinutes: GLOBAL_NOTICE,
      dailyCap: GLOBAL_CAP,
      startIncrementMinutes: GLOBAL_INCREMENT,
    },
  }
}

const ADDRESSED: AdminInvite = {
  id: ADDRESSED_INVITE_ID,
  linkId: LINK_ID,
  linkName: LINK_NAME,
  state: 'open',
  prefill: { name: ADDRESSEE_NAME, email: ADDRESSEE_EMAIL },
  expiresAt: null,
  createdAt: ISSUED_AT,
}

const UNADDRESSED: AdminInvite = {
  id: UNADDRESSED_INVITE_ID,
  linkId: LINK_ID,
  linkName: LINK_NAME,
  state: 'open',
  prefill: {},
  expiresAt: null,
  createdAt: ISSUED_AT,
}

/** An open invite on the second link — the row that makes the Link column load-bearing. */
const ON_OTHER_LINK: AdminInvite = {
  id: OTHER_LINK_INVITE_ID,
  linkId: OTHER_LINK_ID,
  linkName: OTHER_LINK_NAME,
  state: 'open',
  prefill: { name: OTHER_ADDRESSEE_NAME },
  expiresAt: null,
  createdAt: ISSUED_AT,
}

/** Issued while its link was still live, and the link archived afterwards. */
const ON_ARCHIVED_LINK: AdminInvite = {
  id: ARCHIVED_LINK_INVITE_ID,
  linkId: ARCHIVED_LINK_ID,
  linkName: ARCHIVED_LINK_NAME,
  state: 'open',
  prefill: { name: ARCHIVED_ADDRESSEE_NAME },
  expiresAt: null,
  createdAt: ISSUED_AT,
}

const REVOKED: AdminInvite = { ...ADDRESSED, state: 'revoked' }
const SPENT: AdminInvite = { ...UNADDRESSED, id: SPENT_INVITE_ID, state: 'consumed' }
const EXPIRED: AdminInvite = { ...UNADDRESSED, id: EXPIRED_INVITE_ID, state: 'expired' }
const LAPSED: AdminInvite = { ...UNADDRESSED, id: LAPSED_INVITE_ID, state: 'revoked' }

/**
 * Rendered, with the creation panel opened — which is where the form now lives.
 *
 * The wait is on the button losing `disabled` rather than on the picker holding
 * its options, because the picker is inside the panel the button opens: the
 * options do not exist until after the press, and the press does nothing until
 * the links have landed and seeded `linkId`.
 */
async function show() {
  const view = render(<Invites />)

  const open = await screen.findByRole('button', { name: NEW_INVITE })
  await waitFor(() => expect((open as HTMLButtonElement).disabled).toBe(false))
  fireEvent.click(open)

  return view
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(listAllInvites).mockResolvedValue([])
  vi.mocked(listLinks).mockResolvedValue(linksPage([LINK, OTHER_LINK]))
})

afterEach(cleanup)

test('the URL is shown once, at creation, in a field the operator can copy', async () => {
  vi.mocked(createInvite).mockResolvedValue({ id: ADDRESSED_INVITE_ID, url: INVITE_URL })

  await show()

  fireEvent.click(screen.getByRole('button', { name: GENERATE }))

  expect(await screen.findByDisplayValue(INVITE_URL)).toBeTruthy()
  expect(screen.getByText(SHOWN_ONCE)).toBeTruthy()
  // Nothing typed and no deadline chosen is a valid invite: a single-use URL
  // with nothing seeded.
  expect(createInvite).toHaveBeenCalledWith(LINK_ID, { prefill: {}, expiresAt: null })
})

/**
 * The picker is the only thing that says which link an invite is issued against,
 * and the id it carries is what routes the POST. A page that read the first link
 * regardless would look right and issue every invite against the wrong one.
 */
test('generate issues the invite against the link the picker names', async () => {
  vi.mocked(createInvite).mockResolvedValue({ id: OTHER_LINK_INVITE_ID, url: INVITE_URL })

  await show()

  // The picker is behind `Change` now. What this test is about is unchanged: the
  // id the picker carries is what routes the POST.
  fireEvent.click(screen.getByRole('button', { name: CHANGE }))

  fireEvent.change(screen.getByLabelText(LINK_FIELD), { target: { value: OTHER_LINK_ID } })
  fireEvent.click(screen.getByRole('button', { name: GENERATE }))

  await waitFor(() => expect(createInvite).toHaveBeenCalled())
  expect(vi.mocked(createInvite).mock.calls[0][0]).toBe(OTHER_LINK_ID)
})

/**
 * The whole point of the page: one list spanning every link, each row saying
 * which link it is on. Read inside the row rather than on the page, because the
 * picker above prints both names too.
 */
test('the list spans links, and every row names the link it is on', async () => {
  vi.mocked(listAllInvites).mockResolvedValue([ADDRESSED, ON_OTHER_LINK])

  await show()

  const first = (await screen.findByText(ADDRESSEE_NAME)).closest('tr')
  const second = screen.getByText(OTHER_ADDRESSEE_NAME).closest('tr')

  expect(within(first as HTMLTableRowElement).getByText(LINK_NAME)).toBeTruthy()
  expect(within(second as HTMLTableRowElement).getByText(OTHER_LINK_NAME)).toBeTruthy()
})

/**
 * Open by default, because the question the page answers is which URLs are still
 * live — and a used, revoked or expired row is never cleaned up, so All is an
 * archive that only grows.
 */
test('the filter opens on Open, and All is what reveals what is spent', async () => {
  vi.mocked(listAllInvites).mockResolvedValue([ADDRESSED, SPENT, LAPSED, EXPIRED])

  await show()

  expect(await screen.findByText(ADDRESSEE_NAME)).toBeTruthy()
  expect(screen.queryByText(USED_WORD)).toBeNull()
  expect(screen.queryByText(REVOKED_WORD)).toBeNull()
  expect(screen.queryByText(EXPIRED_WORD)).toBeNull()

  fireEvent.click(screen.getByRole('button', { name: ALL_FILTER }))

  expect(screen.getByText(USED_WORD)).toBeTruthy()
  expect(screen.getByText(REVOKED_WORD)).toBeTruthy()
  expect(screen.getByText(EXPIRED_WORD)).toBeTruthy()
})

/**
 * A personal link is an invitation on a link, so with no links there is nothing
 * to issue one against. A picker with no options in it would be a control that
 * looks operable and answers nothing.
 */
test('with no links at all, the page says so instead of offering an empty picker', async () => {
  vi.mocked(listLinks).mockResolvedValue(linksPage([]))

  render(<Invites />)

  expect(await screen.findByText(NO_LIVE_LINK)).toBeTruthy()
  expect((screen.getByRole('button', { name: NEW_INVITE }) as HTMLButtonElement).disabled).toBe(
    true,
  )
  expect(screen.queryByLabelText(LINK_FIELD)).toBeNull()
  expect(screen.queryByRole('button', { name: GENERATE })).toBeNull()
  // And no table under it. `RecordTable` draws its empty state for zero rows
  // regardless, so without the guard the page states two contradictory empties
  // one under the other: make a link first, and no personal links yet.
  expect(screen.queryByText(NO_INVITES)).toBeNull()
})

/**
 * Archived links are in the answer — `listLinks` is where one is brought back
 * from — and offering one would mint a URL that is valid and then 404s: nothing
 * on the invite path checks `active`, only the booking page does.
 */
test('the picker offers live links only', async () => {
  vi.mocked(listLinks).mockResolvedValue(linksPage([LINK, ARCHIVED_LINK]))

  await show()

  fireEvent.click(screen.getByRole('button', { name: CHANGE }))

  expect(screen.getByRole('option', { name: LINK_NAME })).toBeTruthy()
  expect(screen.queryByRole('option', { name: ARCHIVED_LINK_NAME })).toBeNull()
})

test('with every link archived, the page names both recoveries', async () => {
  vi.mocked(listLinks).mockResolvedValue(linksPage([ARCHIVED_LINK]))

  render(<Invites />)

  expect(await screen.findByText(NO_LIVE_LINK)).toBeTruthy()
  expect((screen.getByRole('button', { name: NEW_INVITE }) as HTMLButtonElement).disabled).toBe(
    true,
  )
  expect(screen.queryByLabelText(LINK_FIELD)).toBeNull()
  expect(screen.queryByText(NO_INVITES)).toBeNull()
})

/**
 * The picker filters, the list does not. An invite issued before its link was
 * archived is still worth seeing, and revoke routes by the invite's own link, so
 * taking it back still works.
 */
test('an invite on an archived link is still listed, and still revocable', async () => {
  vi.mocked(listAllInvites).mockResolvedValue([ON_ARCHIVED_LINK])
  vi.mocked(revokeInvite).mockResolvedValue(undefined)

  await show()

  const row = (await screen.findByText(ARCHIVED_ADDRESSEE_NAME)).closest('tr')
  expect(within(row as HTMLTableRowElement).getByText(ARCHIVED_LINK_NAME)).toBeTruthy()

  fireEvent.click(within(row as HTMLTableRowElement).getByRole('button', { name: REVOKE }))

  await waitFor(() =>
    expect(revokeInvite).toHaveBeenCalledWith(ARCHIVED_LINK_ID, ARCHIVED_LINK_INVITE_ID),
  )
})

/**
 * A links read that never lands leaves nothing to seed `linkId` from, and there
 * is no empty state for a state nobody has established — so the page draws no
 * explanation, only a dead button. It has to be dead: a panel opened there can
 * only POST to `/admin/links//invites` and stack a second, unrelated failure on
 * the first.
 */
test('a failed links read leaves the panel unopenable rather than posting to no link', async () => {
  vi.mocked(listLinks).mockRejectedValue(
    new ApiError(HTTP_SERVER_ERROR, LINKS_UNREADABLE, LINKS_UNREADABLE_MESSAGE),
  )

  render(<Invites />)

  expect(await screen.findByText(LINKS_UNREADABLE_MESSAGE)).toBeTruthy()

  const open = screen.getByRole('button', { name: NEW_INVITE }) as HTMLButtonElement
  expect(open.disabled).toBe(true)

  fireEvent.click(open)
  expect(screen.queryByRole('button', { name: GENERATE })).toBeNull()
  expect(createInvite).not.toHaveBeenCalled()
})

/**
 * The announcement and the focus a server-rendered page got for free. A field
 * that mounts with its text already in it is not reliably announced, so the URL
 * is put under the cursor instead — and `onFocus` selects it.
 */
test('the URL field takes focus the moment it appears', async () => {
  vi.mocked(createInvite).mockResolvedValue({ id: ADDRESSED_INVITE_ID, url: INVITE_URL })

  await show()

  fireEvent.click(screen.getByRole('button', { name: GENERATE }))

  const field = await screen.findByDisplayValue(INVITE_URL)

  // `waitFor`, not a bare assertion: the field is in the DOM one commit before
  // the effect that focuses it has flushed, and with two reads in flight the
  // scheduler occasionally hands the poller that in-between state. Measured —
  // one failure in fifty runs of the bare form, none in fifty of this one.
  await waitFor(() => expect(document.activeElement).toBe(field))
})

test('a deadline preset travels as an instant, because the wire carries a moment', async () => {
  vi.mocked(createInvite).mockResolvedValue({ id: ADDRESSED_INVITE_ID, url: INVITE_URL })

  await show()

  // The picker is behind `Change` now. What this test is about is unchanged: the
  // id the picker carries is what routes the POST.
  fireEvent.click(screen.getByRole('button', { name: CHANGE }))

  fireEvent.change(screen.getByLabelText(DEADLINE_FIELD), {
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
  vi.mocked(listAllInvites).mockResolvedValue([UNADDRESSED])

  await show()

  expect(await screen.findByText(UNADDRESSED_TITLE)).toBeTruthy()
  expect(screen.getByText(UNADDRESSED_DETAIL)).toBeTruthy()
})

test('a prefilled invite names who it is for', async () => {
  vi.mocked(listAllInvites).mockResolvedValue([ADDRESSED])

  await show()

  expect(await screen.findByText(ADDRESSEE_NAME)).toBeTruthy()
  expect(screen.getByText(ADDRESSEE_EMAIL)).toBeTruthy()
})

/**
 * The security property, and the reason `InviteView` has no field for a URL: the
 * server keeps a SHA-256 of the token and nothing else, so a list that appeared
 * to show one would be showing something it invented.
 */
test('no row in the list carries a URL, because there is none left to show', async () => {
  vi.mocked(listAllInvites).mockResolvedValue([ADDRESSED, UNADDRESSED])

  const { container } = await show()

  await screen.findByText(ADDRESSEE_NAME)

  expect(container.textContent).not.toContain(TOKEN_QUERY)
  expect(container.querySelector('input[readonly]')).toBeNull()
  expect(screen.queryByDisplayValue(INVITE_URL)).toBeNull()
})

/**
 * Revoke takes the invite back, and under the default filter the row leaves the
 * list with it — a revoked invite is no longer one of the URLs still live. All
 * is where it went, and there it draws neither button.
 */
test('revoke takes the invite back, and the refreshed row reads revoked', async () => {
  vi.mocked(listAllInvites).mockResolvedValueOnce([ADDRESSED]).mockResolvedValue([REVOKED])
  vi.mocked(revokeInvite).mockResolvedValue(undefined)

  await show()

  fireEvent.click(await screen.findByRole('button', { name: REVOKE }))

  await waitFor(() => expect(screen.queryByText(ADDRESSEE_NAME)).toBeNull())
  expect(revokeInvite).toHaveBeenCalledWith(LINK_ID, ADDRESSED_INVITE_ID)

  fireEvent.click(screen.getByRole('button', { name: ALL_FILTER }))

  expect(screen.getByText(REVOKED_WORD)).toBeTruthy()
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
  vi.mocked(listAllInvites).mockResolvedValue([UNADDRESSED, ADDRESSED])
  vi.mocked(createInvite).mockResolvedValue({ id: UNADDRESSED_INVITE_ID, url: INVITE_URL })
  vi.mocked(revokeInvite).mockResolvedValue(undefined)

  await show()

  fireEvent.click(screen.getByRole('button', { name: GENERATE }))
  expect(await screen.findByDisplayValue(INVITE_URL)).toBeTruthy()

  const neighbour = screen.getByText(ADDRESSEE_NAME).closest('tr')
  fireEvent.click(within(neighbour as HTMLTableRowElement).getByRole('button', { name: REVOKE }))

  await waitFor(() => expect(revokeInvite).toHaveBeenCalledWith(LINK_ID, ADDRESSED_INVITE_ID))
  expect(screen.getByDisplayValue(INVITE_URL)).toBeTruthy()
})

test('revoking the invite on show takes its URL away, because that URL is dead', async () => {
  vi.mocked(listAllInvites)
    .mockResolvedValueOnce([ADDRESSED])
    .mockResolvedValueOnce([ADDRESSED])
    .mockResolvedValue([REVOKED])
  vi.mocked(createInvite).mockResolvedValue({ id: ADDRESSED_INVITE_ID, url: INVITE_URL })
  vi.mocked(revokeInvite).mockResolvedValue(undefined)

  await show()

  fireEvent.click(screen.getByRole('button', { name: GENERATE }))
  expect(await screen.findByDisplayValue(INVITE_URL)).toBeTruthy()

  fireEvent.click(screen.getByRole('button', { name: REVOKE }))

  await waitFor(() => expect(screen.queryByDisplayValue(INVITE_URL)).toBeNull())
})

/**
 * Revoke and regenerate are routed under the invite's own link, never under the
 * one the picker is holding — the picker says what the next invite will be
 * issued against, and a row on this page is very often not on that link.
 */
test('a row on another link revokes under that link, not under the picker', async () => {
  vi.mocked(listAllInvites).mockResolvedValue([ON_OTHER_LINK])
  vi.mocked(revokeInvite).mockResolvedValue(undefined)

  await show()

  fireEvent.click(await screen.findByRole('button', { name: REVOKE }))

  await waitFor(() =>
    expect(revokeInvite).toHaveBeenCalledWith(OTHER_LINK_ID, OTHER_LINK_INVITE_ID),
  )
})

test('a refused generate says why, in the words the server used', async () => {
  vi.mocked(createInvite).mockRejectedValue(
    new ApiError(HTTP_BAD_REQUEST, PREFILL_INVALID, PREFILL_INVALID_MESSAGE),
  )

  await show()

  fireEvent.change(screen.getByLabelText(NAME_FIELD), { target: { value: ADDRESSEE_NAME } })
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
  vi.mocked(listAllInvites).mockResolvedValue([ADDRESSED])
  vi.mocked(createInvite).mockResolvedValue({ id: ADDRESSED_INVITE_ID, url: INVITE_URL })
  vi.mocked(regenerateInvite).mockResolvedValue({
    id: REPLACEMENT_INVITE_ID,
    url: REPLACEMENT_URL,
  })

  await show()

  fireEvent.click(screen.getByRole('button', { name: GENERATE }))
  expect(await screen.findByDisplayValue(INVITE_URL)).toBeTruthy()

  fireEvent.click(screen.getByRole('button', { name: REGENERATE }))

  expect(await screen.findByDisplayValue(REPLACEMENT_URL)).toBeTruthy()
  expect(screen.queryByDisplayValue(INVITE_URL)).toBeNull()
  expect(regenerateInvite).toHaveBeenCalledWith(LINK_ID, ADDRESSED_INVITE_ID)
})

/**
 * Two empties, and they are not the same sentence: nothing issued on any link is
 * a state the filter cannot change, while nothing open right now is one the
 * filter is hiding rows behind — so only the second names the filter.
 */
test('a filtered-out list says the rows are behind All, not that there are none', async () => {
  vi.mocked(listAllInvites).mockResolvedValue([SPENT])

  await show()

  expect(await screen.findByText(NONE_OPEN)).toBeTruthy()

  fireEvent.click(screen.getByRole('button', { name: ALL_FILTER }))
  expect(screen.getByText(USED_WORD)).toBeTruthy()

  fireEvent.click(screen.getByRole('button', { name: OPEN_FILTER }))
  expect(screen.getByText(NONE_OPEN)).toBeTruthy()
})

test('with nothing issued on any link, there is no filter and no talk of one', async () => {
  await show()

  expect(await screen.findByText(NO_INVITES)).toBeTruthy()
  expect(screen.queryByRole('button', { name: ALL_FILTER })).toBeNull()
})

/**
 * At rest the page is heading, filter, table. The form card was open whether or
 * not anybody was issuing an invite, and it is the largest thing on a page whose
 * subject is the table.
 */
test('the form is not on the page until the button asks for it', async () => {
  render(<Invites />)

  const open = await screen.findByRole('button', { name: NEW_INVITE })

  expect(screen.queryByLabelText(NAME_FIELD)).toBeNull()
  expect(screen.queryByRole('button', { name: GENERATE })).toBeNull()
  expect(open.getAttribute(EXPANDED)).toBe('false')
})

/**
 * The two fields that change every time are asked; the two that are usually
 * already right are stated. With one link and no deadline as the overwhelming
 * case, four questions to ask two is the wrong trade.
 */
test('the panel asks for a person and states the link and the deadline', async () => {
  await show()

  expect(screen.getByLabelText(NAME_FIELD)).toBeTruthy()
  expect(screen.getByLabelText(EMAIL_FIELD)).toBeTruthy()

  expect(screen.queryByLabelText(LINK_FIELD)).toBeNull()
  expect(screen.queryByLabelText(DEADLINE_FIELD)).toBeNull()

  expect(screen.getByText(LINK_NAME)).toBeTruthy()
  expect(screen.getByText(NO_DEADLINE_WORD)).toBeTruthy()
})

/**
 * The risk this shape takes on. The picker is hidden and there is more than one
 * link, so the failure it invites is minting against the wrong one and not
 * learning about it until the recipient books the wrong meeting. The link name
 * is the one phrase in the line that is not quiet.
 *
 * Switched to the second link, and switched back out of `Change`, for two
 * separate reasons. Reading the sentence off the seeded first link passes just
 * as well when the sentence ignores `linkId` and prints `offered[0]` — which is
 * the bug this test exists to catch. And the picker's own <option> carries the
 * same name, so the assertion is only unambiguous once the controls are
 * collapsed again.
 */
test('the link the invite will be issued against is the loud half of the sentence', async () => {
  await show()

  fireEvent.click(screen.getByRole('button', { name: CHANGE }))
  fireEvent.change(screen.getByLabelText(LINK_FIELD), { target: { value: OTHER_LINK_ID } })
  fireEvent.click(screen.getByRole('button', { name: CHANGE }))

  expect(screen.getByText(OTHER_LINK_NAME).tagName).toBe('STRONG')
})

test('Change is what puts the link and the deadline back on the page', async () => {
  await show()

  const change = screen.getByRole('button', { name: CHANGE })
  expect(change.getAttribute(EXPANDED)).toBe('false')

  fireEvent.click(change)

  expect(change.getAttribute(EXPANDED)).toBe('true')
  expect(screen.getByLabelText(LINK_FIELD)).toBeTruthy()
  expect(screen.getByLabelText(DEADLINE_FIELD)).toBeTruthy()
})

/**
 * DESIGN.md allows one primary per view and gives retreats the ghost treatment.
 * Open, the panel owns the primary — Generate is what commits — and the header
 * button is the way back out.
 */
test('the panel owns the only primary while it is open, and Cancel is a retreat', async () => {
  render(<Invites />)

  const open = await screen.findByRole('button', { name: NEW_INVITE })
  await waitFor(() => expect((open as HTMLButtonElement).disabled).toBe(false))
  expect(open.className).toContain(PRIMARY)

  fireEvent.click(open)

  const close = screen.getByRole('button', { name: CANCEL })
  expect(close.className).not.toContain(PRIMARY)
  expect(close.className).toContain(GHOST)
  expect(screen.getByRole('button', { name: GENERATE }).className).toContain(PRIMARY)
})

test('the button that opens the panel is the button that closes it', async () => {
  await show()

  expect(screen.getByLabelText(NAME_FIELD)).toBeTruthy()

  const close = screen.getByRole('button', { name: CANCEL })
  expect(close.getAttribute(EXPANDED)).toBe('true')

  fireEvent.click(close)

  expect(screen.queryByLabelText(NAME_FIELD)).toBeNull()
  expect(screen.getByRole('button', { name: NEW_INVITE })).toBeTruthy()
})

/**
 * Regenerate mints a URL that exists exactly once — only its SHA-256 is stored —
 * and the panel is the only place a URL is ever shown. Pressed from a row with
 * the panel closed, which is the ordinary way it is pressed, the URL would
 * otherwise be minted and thrown away in the same tick.
 */
test('regenerating from a closed panel opens it on the URL it just minted', async () => {
  vi.mocked(listAllInvites).mockResolvedValue([ADDRESSED])
  vi.mocked(regenerateInvite).mockResolvedValue({
    id: REPLACEMENT_INVITE_ID,
    url: REPLACEMENT_URL,
  })

  render(<Invites />)

  fireEvent.click(await screen.findByRole('button', { name: REGENERATE }))

  expect(await screen.findByDisplayValue(REPLACEMENT_URL)).toBeTruthy()
})

/**
 * A className this page invents but the stylesheet does not have renders as
 * unstyled markup that no other test can see. Scoped to the header and the
 * panel — the two subtrees this page builds itself — rather than to the whole
 * document, which would be re-asserting what RecordTable's own tests cover.
 */
test('every class the header and the panel render exists in kairos.css', async () => {
  const css = readFileSync(STYLESHEET, 'utf8')
  const { container } = await show()

  const roots = [container.querySelector(`.${PAGE_HEADER}`), container.querySelector(`#${PANEL_ID}`)]
  const used = new Set(
    roots
      .filter((root): root is Element => root !== null)
      .flatMap((root) => [root, ...root.querySelectorAll('[class]')])
      .flatMap((element) => [...element.classList]),
  )

  expect([...used].filter((name) => !css.includes(`.${name}`))).toEqual([])
})
