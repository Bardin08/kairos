import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'

import LinkEditor from '../src/routes/admin/LinkEditor'
import type { AdminLink, AdminPattern, GlobalLimits } from '../src/routes/admin/adminLinks'

const GLOBAL_MARGIN = 15
const GLOBAL_NOTICE = 120
const GLOBAL_CAP = 4
const GLOBAL_INCREMENT = 30
const LINK_MARGIN = 30
const SHORT_CALL = 25
const LONG_CALL = 50

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

function show(link: AdminLink | null = LINK) {
  return render(
    <LinkEditor
      link={link}
      patterns={PATTERNS}
      globals={GLOBALS}
      onSaved={vi.fn()}
      onCancel={vi.fn()}
    />,
  )
}

afterEach(cleanup)

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

test('discard puts every field back and disables save again', () => {
  show()

  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Consulting' } })
  fireEvent.click(screen.getByRole('button', { name: 'Discard' }))

  expect((screen.getByLabelText('Name') as HTMLInputElement).value).toBe(LINK.name)
  expect(screen.getByText('No unsaved changes')).toBeTruthy()
})

test('the address is shown as the whole URL, not as its last word', () => {
  show()

  // The operator setting this is deciding what to paste into a chat, and they
  // cannot judge `consulting` without the part in front of it.
  expect(screen.getByText(`${window.location.origin}/book/`)).toBeTruthy()
})

test('a link that has never been saved offers no copy button, because there is nothing to copy', () => {
  show(null)

  expect(screen.queryByRole('button', { name: 'Copy link address' })).toBeNull()
})

test('archiving states its consequence before the button, not after the fact', () => {
  show()

  expect(screen.getByText(/answers as though it never existed/)).toBeTruthy()
  expect(screen.getByRole('button', { name: 'Archive link' })).toBeTruthy()
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

test('discard is disabled when nothing has changed', () => {
  show()

  expect((screen.getByRole('button', { name: 'Discard' }) as HTMLButtonElement).disabled).toBe(
    true,
  )
})
