import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'

import { ApiError } from '../src/lib/api'
import * as adminLinks from '../src/routes/admin/adminLinks'
import type { AdminLink, AdminPattern, GlobalLimits } from '../src/routes/admin/adminLinks'
import LinkEditor from '../src/routes/admin/LinkEditor'

vi.mock('../src/routes/admin/adminLinks', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../src/routes/admin/adminLinks')>()
  return {
    ...actual,
    createLink: vi.fn(),
    updateLink: vi.fn(),
    updateLinkLimits: vi.fn(),
    archiveLink: vi.fn(),
    restoreLink: vi.fn(),
  }
})

const GLOBAL_MARGIN = 15
const GLOBAL_NOTICE = 120
const GLOBAL_CAP = 4
const GLOBAL_INCREMENT = 30
const LINK_MARGIN = 30
const SHORT_CALL = 25
const LONG_CALL = 50

/** What the server hands back for the link's own id, on a successful update. Deliberately not `l1` — the save path must use this, not the link it was called with. */
const SERVER_ID = 'server-assigned-id'

const HTTP_BAD_REQUEST = 400

const PATTERNS: AdminPattern[] = [
  { id: 'p1', name: 'Weekdays' },
  { id: 'p2', name: 'Evenings' },
]

const GLOBALS: GlobalLimits = {
  marginMinutes: GLOBAL_MARGIN,
  minNoticeMinutes: GLOBAL_NOTICE,
  dailyCap: GLOBAL_CAP,
  startIncrementMinutes: GLOBAL_INCREMENT,
}

const LINK: AdminLink = {
  id: 'l1',
  slug: 'consulting',
  name: 'Consulting call',
  description: 'A chat about your system.',
  patternId: 'p1',
  patternName: 'Weekdays',
  active: true,
  durations: [SHORT_CALL, LONG_CALL],
  marginMinutes: LINK_MARGIN,
  minNoticeMinutes: null,
  dailyCap: null,
  startIncrementMinutes: null,
}

const ARCHIVED_LINK: AdminLink = {
  ...LINK,
  id: 'l2',
  slug: 'retired-call',
  name: 'Retired call',
  active: false,
}

function show(
  link: AdminLink | null = LINK,
  options: { onSaved?: () => void; onCancel?: () => void; publicBaseUrl?: string | null } = {},
) {
  return render(
    <LinkEditor
      link={link}
      patterns={PATTERNS}
      globals={GLOBALS}
      publicBaseUrl={options.publicBaseUrl ?? null}
      onSaved={options.onSaved ?? vi.fn()}
      onCancel={options.onCancel ?? vi.fn()}
    />,
  )
}

afterEach(() => {
  cleanup()
  vi.resetAllMocks()
})

test('an untouched editor cannot be saved and says so', () => {
  show()

  expect(screen.getByText('No unsaved changes')).toBeTruthy()
  expect((screen.getByRole('button', { name: 'Save link' }) as HTMLButtonElement).disabled).toBe(
    true,
  )
})

test('the count follows the number of fields that moved', () => {
  show()

  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Consulting' } })
  expect(screen.getByText('1 unsaved change')).toBeTruthy()

  fireEvent.change(screen.getByLabelText('Address'), { target: { value: 'chat' } })
  expect(screen.getByText('2 unsaved changes')).toBeTruthy()
})

test('discard puts every kind of field back — text, a duration, and a limit override — and disables save again', () => {
  show()

  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Consulting, revised' } })
  fireEvent.click(screen.getByRole('button', { name: `Remove ${SHORT_CALL} minutes` }))
  fireEvent.click(screen.getByRole('button', { name: 'Override Minimum notice' }))

  expect(screen.getByText('3 unsaved changes')).toBeTruthy()

  fireEvent.click(screen.getByRole('button', { name: 'Discard' }))

  expect((screen.getByLabelText('Name') as HTMLInputElement).value).toBe(LINK.name)
  expect(screen.getByRole('button', { name: `Remove ${SHORT_CALL} minutes` })).toBeTruthy()
  expect(screen.getByRole('button', { name: 'Override Minimum notice' })).toBeTruthy()
  expect(screen.queryByRole('button', { name: 'Use global Minimum notice' })).toBeNull()
  expect(screen.getByText('No unsaved changes')).toBeTruthy()
})

test('discard is disabled when nothing has changed', () => {
  show()

  expect((screen.getByRole('button', { name: 'Discard' }) as HTMLButtonElement).disabled).toBe(
    true,
  )
})

test('the address prefix shows the host and path, not just the slug, and drops the scheme', () => {
  show()

  // The operator setting this is deciding what to paste into a chat, and they
  // cannot judge `consulting` without the host and path in front of it. The scheme
  // is left off the display, the way the browser's own address bar leaves it off.
  expect(screen.getByText(`${window.location.host}/book/`)).toBeTruthy()
  expect(screen.queryByText(/http:\/\//)).toBeNull()
})

test('a link that has never been saved offers no copy button, because there is nothing to copy', () => {
  show(null)

  expect(screen.queryByRole('button', { name: 'Copy link address' })).toBeNull()
})

test('the Copy button flips to Copied, and reverts once the address is edited again', () => {
  show()

  fireEvent.click(screen.getByRole('button', { name: 'Copy link address' }))
  expect(screen.getByRole('button', { name: 'Copy link address' }).textContent).toBe('Copied')

  fireEvent.change(screen.getByLabelText('Address'), { target: { value: 'new-slug' } })
  expect(screen.getByRole('button', { name: 'Copy link address' }).textContent).toBe('Copy')
})

test('the address input carries no fixed size class, so it can grow to fill the column', () => {
  show()

  // ctl-md fixed it at 288px, well short of the column — the bug this test
  // guards against. Removing the class is what lets .prefix's own flex and
  // min-width: 0 (kairos.css) take over sizing it instead.
  expect(screen.getByLabelText('Address').classList.contains('ctl-md')).toBe(false)
})

test('Copy sits on the label line, and the help text and control stay below it, in order', () => {
  show()

  const label = screen.getByText('Address')
  const row = label.parentElement
  const button = screen.getByRole('button', { name: 'Copy link address' })
  const help = screen.getByText('Lowercase words joined by hyphens.')

  // Same row as the label — not the control row below, which is exactly the
  // prefix and the input with nothing else to make it wrap.
  expect(button.parentElement).toBe(row)
  expect(row?.nextElementSibling).toBe(help)
  expect(help.nextElementSibling?.classList.contains('prefix')).toBe(true)
})

test('the copied address keeps its scheme even though the display drops it', () => {
  show()

  fireEvent.click(screen.getByRole('button', { name: 'Copy link address' }))

  // The display above shortens to host + path; what lands on the clipboard must
  // still be the full absolute URL, scheme included, or a paste into a chat is a
  // link nothing can follow.
  expect(navigator.clipboard.writeText).toHaveBeenCalledWith(
    `${window.location.origin}/book/${LINK.slug}`,
  )
})

/**
 * jsdom serves this suite from http://localhost:3000. PUBLIC_ORIGIN is deliberately a
 * different host: the two origins differing IS the bug under test, and a fixture where
 * they match would pass against the old code that read the browser's origin.
 */
const PUBLIC_ORIGIN = 'https://kairos.example.test'
const PUBLIC_HOST = 'kairos.example.test'

test('the copied address uses the configured public origin, not the browser origin', () => {
  show(LINK, { publicBaseUrl: PUBLIC_ORIGIN })

  fireEvent.click(screen.getByRole('button', { name: 'Copy link address' }))

  expect(PUBLIC_ORIGIN).not.toBe(window.location.origin)
  expect(navigator.clipboard.writeText).toHaveBeenCalledWith(
    `${PUBLIC_ORIGIN}/book/${LINK.slug}`,
  )
})

test('the slug prefix shows the configured public host', () => {
  show(LINK, { publicBaseUrl: PUBLIC_ORIGIN })

  expect(screen.getByText(`${PUBLIC_HOST}/book/`)).toBeTruthy()
})

test('with no public origin configured both fall back to the browser', () => {
  show(LINK, { publicBaseUrl: null })

  fireEvent.click(screen.getByRole('button', { name: 'Copy link address' }))

  expect(navigator.clipboard.writeText).toHaveBeenCalledWith(
    `${window.location.origin}/book/${LINK.slug}`,
  )
  expect(screen.getByText(`${window.location.host}/book/`)).toBeTruthy()
})

test('archiving states its consequence before the button, not after the fact', () => {
  show()

  const consequence = screen.getByText(/answers as though it never existed/)
  const button = screen.getByRole('button', { name: 'Archive link' })

  const buttonFollowsConsequence = Boolean(
    consequence.compareDocumentPosition(button) & Node.DOCUMENT_POSITION_FOLLOWING,
  )
  expect(buttonFollowsConsequence).toBe(true)
})

test('overriding a limit registers as dirty', () => {
  show()

  expect(screen.getByText('No unsaved changes')).toBeTruthy()

  fireEvent.click(screen.getByRole('button', { name: 'Override Minimum notice' }))

  expect(screen.getByText('1 unsaved change')).toBeTruthy()
})

test('a new link starts clean and offers no Archive card', () => {
  show(null)

  expect(screen.getByText('No unsaved changes')).toBeTruthy()
  expect((screen.getByRole('button', { name: 'Save link' }) as HTMLButtonElement).disabled).toBe(
    true,
  )
  expect(screen.queryByRole('button', { name: 'Archive link' })).toBeNull()
  expect(screen.queryByRole('button', { name: 'Restore link' })).toBeNull()
})

test('an archived link offers Restore rather than Archive, and states the consequence', () => {
  show(ARCHIVED_LINK)

  expect(screen.queryByRole('button', { name: 'Archive link' })).toBeNull()
  expect(screen.getByRole('button', { name: 'Restore link' })).toBeTruthy()
  expect(screen.getByText(/answers as though it never existed/)).toBeTruthy()
})

test('archive is disabled while the form is dirty, and the card says why', () => {
  show()

  expect(screen.queryByText(/Archiving discards them/)).toBeNull()

  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Consulting, revised' } })

  expect(screen.getByText(/Archiving discards them/)).toBeTruthy()
  expect((screen.getByRole('button', { name: 'Archive link' }) as HTMLButtonElement).disabled).toBe(
    true,
  )
})

test('restore is disabled while the form is dirty, and the card says why', () => {
  show(ARCHIVED_LINK)

  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Retired call, revised' } })

  expect(screen.getByText(/Restoring discards them/)).toBeTruthy()
  expect((screen.getByRole('button', { name: 'Restore link' }) as HTMLButtonElement).disabled).toBe(
    true,
  )
})

test('a successful save writes the link, then the limits, using the id the first write returned, with null limits intact', async () => {
  const onSaved = vi.fn()
  vi.mocked(adminLinks.updateLink).mockResolvedValue({ ...LINK, id: SERVER_ID })
  vi.mocked(adminLinks.updateLinkLimits).mockResolvedValue({ ...LINK, id: SERVER_ID })

  show(LINK, { onSaved })

  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Consulting, revised' } })
  fireEvent.click(screen.getByRole('button', { name: 'Save link' }))

  await waitFor(() => expect(onSaved).toHaveBeenCalled())

  expect(adminLinks.updateLink).toHaveBeenCalledWith(LINK.id, {
    slug: LINK.slug,
    name: 'Consulting, revised',
    description: LINK.description,
    patternId: LINK.patternId,
    durations: LINK.durations,
  })
  // The id that reaches this call is the server's, from the update's own
  // response — not `LINK.id`, which the component must not reuse here.
  expect(adminLinks.updateLinkLimits).toHaveBeenCalledWith(SERVER_ID, {
    marginMinutes: LINK.marginMinutes,
    minNoticeMinutes: null,
    dailyCap: null,
    startIncrementMinutes: null,
  })

  const updateOrder = vi.mocked(adminLinks.updateLink).mock.invocationCallOrder[0]
  const limitsOrder = vi.mocked(adminLinks.updateLinkLimits).mock.invocationCallOrder[0]
  expect(updateOrder).toBeLessThan(limitsOrder as number)
})

test('a failed save renders the server failure and leaves the form usable', async () => {
  const FAILURE_MESSAGE = 'That address is already in use.'
  vi.mocked(adminLinks.updateLink).mockRejectedValue(
    new ApiError(HTTP_BAD_REQUEST, 'slug_taken', FAILURE_MESSAGE),
  )

  show()

  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Consulting, revised' } })
  fireEvent.click(screen.getByRole('button', { name: 'Save link' }))

  expect(await screen.findByText(FAILURE_MESSAGE)).toBeTruthy()
  expect((screen.getByRole('button', { name: 'Save link' }) as HTMLButtonElement).disabled).toBe(
    false,
  )
})

test('archive calls archiveLink and then onSaved', async () => {
  const onSaved = vi.fn()
  vi.mocked(adminLinks.archiveLink).mockResolvedValue({ ...LINK, active: false })

  show(LINK, { onSaved })

  fireEvent.click(screen.getByRole('button', { name: 'Archive link' }))

  await waitFor(() => expect(onSaved).toHaveBeenCalled())
  expect(adminLinks.archiveLink).toHaveBeenCalledWith(LINK.id)
})

/**
 * Finding 12. Copy built its string from the draft, so an address typed and not
 * yet saved was copied as though it resolved — the same 404 the button already
 * refuses to hand out for a link that was never saved at all, reached by the
 * other route.
 */
test('Copy hands out the saved address, not the one being typed', () => {
  show()

  fireEvent.change(screen.getByLabelText('Address'), { target: { value: 'renamed-not-saved' } })
  fireEvent.click(screen.getByRole('button', { name: 'Copy link address' }))

  expect(navigator.clipboard.writeText).toHaveBeenCalledWith(
    `${window.location.origin}/book/${LINK.slug}`,
  )
})
