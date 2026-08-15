import { expect, test } from 'vitest'

import type { AdminLink } from '../src/routes/admin/adminLinks'
import { changedCount, draftOf, unsavedLabel, type EditorDraft } from '../src/routes/admin/editorDraft'

// A link that offers a short call and a long one, overrides its margin, and
// inherits the other three. Named rather than inlined so a reader of an
// assertion below knows which value moved and why it matters.
const SHORT_CALL = 25
const LONG_CALL = 50
const QUARTER_HOUR = 15
const HALF_HOUR = 30
const THREE_QUARTER_HOUR = 45
const MARGIN_OVERRIDE = 30
const NO_NOTICE = 0

const CLEAN = 0
const ONE_FIELD = 1
const TWO_FIELDS = 2

const BASE: EditorDraft = {
  name: 'Consulting call',
  slug: 'consulting',
  description: 'A chat about your system.',
  patternId: 'pattern-1',
  durations: [SHORT_CALL, LONG_CALL],
  limits: {
    marginMinutes: MARGIN_OVERRIDE,
    minNoticeMinutes: null,
    dailyCap: null,
    startIncrementMinutes: null,
  },
}

test('an untouched draft is clean', () => {
  expect(changedCount(BASE, { ...BASE })).toBe(CLEAN)
})

test('each changed field counts once', () => {
  expect(changedCount(BASE, { ...BASE, name: 'Consulting' })).toBe(ONE_FIELD)
  expect(changedCount(BASE, { ...BASE, name: 'Consulting', slug: 'chat' })).toBe(TWO_FIELDS)
})

test('reordering the durations is a change, because the order is the booking page tab order', () => {
  expect(changedCount(BASE, { ...BASE, durations: [LONG_CALL, SHORT_CALL] })).toBe(ONE_FIELD)
})

test('the durations count once however many of them changed', () => {
  const replaced = [QUARTER_HOUR, HALF_HOUR, THREE_QUARTER_HOUR]
  expect(changedCount(BASE, { ...BASE, durations: replaced })).toBe(ONE_FIELD)
})

test('inheriting a limit that was overridden is a change', () => {
  const draft = { ...BASE, limits: { ...BASE.limits, marginMinutes: null } }
  expect(changedCount(BASE, draft)).toBe(ONE_FIELD)
})

test('an explicit zero is not the same as inheriting', () => {
  const inherited = { ...BASE, limits: { ...BASE.limits, minNoticeMinutes: null } }
  const zero = { ...BASE, limits: { ...BASE.limits, minNoticeMinutes: NO_NOTICE } }

  // The distinction the whole limits design turns on. A comparison written with
  // `||` or `??` anywhere in it would call these equal and leave the save
  // button dead on a change the operator can see on screen.
  expect(changedCount(inherited, zero)).toBe(ONE_FIELD)
})

// --- draftOf -----------------------------------------------------------

const EXISTING_LINK_ID = 'link-1'
const EXISTING_LINK_NAME = 'Discovery call'
const EXISTING_LINK_SLUG = 'discovery'
const EXISTING_LINK_DESCRIPTION = 'First conversation about your project.'
const EXISTING_PATTERN_ID = 'pattern-9'
const EXISTING_PATTERN_NAME = 'Weekday mornings'
const EXISTING_MARGIN = 10
const EXISTING_NOTICE = 60
const NEW_LINK_FIRST_PATTERN_ID = 'pattern-first'
const EXPECTED_NEW_LINK_DURATION_COUNT = 1
const ONE_MORE_DURATION = 20
const REPLACED_NAME = 'Renamed on the draft only'

const EXISTING_LINK: AdminLink = {
  id: EXISTING_LINK_ID,
  slug: EXISTING_LINK_SLUG,
  name: EXISTING_LINK_NAME,
  description: EXISTING_LINK_DESCRIPTION,
  patternId: EXISTING_PATTERN_ID,
  patternName: EXISTING_PATTERN_NAME,
  active: true,
  durations: [SHORT_CALL, LONG_CALL],
  marginMinutes: EXISTING_MARGIN,
  minNoticeMinutes: EXISTING_NOTICE,
  dailyCap: null,
  startIncrementMinutes: null,
}

test('draftOf an existing link carries its nine editable values', () => {
  const draft = draftOf(EXISTING_LINK, NEW_LINK_FIRST_PATTERN_ID)

  expect(draft).toEqual<EditorDraft>({
    name: EXISTING_LINK_NAME,
    slug: EXISTING_LINK_SLUG,
    description: EXISTING_LINK_DESCRIPTION,
    patternId: EXISTING_PATTERN_ID,
    durations: [SHORT_CALL, LONG_CALL],
    limits: {
      marginMinutes: EXISTING_MARGIN,
      minNoticeMinutes: EXISTING_NOTICE,
      dailyCap: null,
      startIncrementMinutes: null,
    },
  })
})

test('draftOf null starts a new link with every limit inherited and one duration', () => {
  const draft = draftOf(null, NEW_LINK_FIRST_PATTERN_ID)

  expect(draft.patternId).toBe(NEW_LINK_FIRST_PATTERN_ID)
  expect(draft.durations).toHaveLength(EXPECTED_NEW_LINK_DURATION_COUNT)
  expect(draft.limits).toEqual({
    marginMinutes: null,
    minNoticeMinutes: null,
    dailyCap: null,
    startIncrementMinutes: null,
  })
})

test('draftOf compared against itself is clean', () => {
  const draft = draftOf(EXISTING_LINK, NEW_LINK_FIRST_PATTERN_ID)
  expect(changedCount(draft, draftOf(EXISTING_LINK, NEW_LINK_FIRST_PATTERN_ID))).toBe(CLEAN)
})

test('mutating the draft cannot reach back into the AdminLink it came from', () => {
  const draft = draftOf(EXISTING_LINK, NEW_LINK_FIRST_PATTERN_ID)

  draft.durations.push(ONE_MORE_DURATION)
  draft.limits.marginMinutes = null
  draft.name = REPLACED_NAME

  expect(EXISTING_LINK.durations).toEqual([SHORT_CALL, LONG_CALL])
  expect(EXISTING_LINK.marginMinutes).toBe(EXISTING_MARGIN)
  expect(EXISTING_LINK.name).toBe(EXISTING_LINK_NAME)
})

// --- unsavedLabel --------------------------------------------------------

test('unsavedLabel with nothing changed', () => {
  expect(unsavedLabel(CLEAN)).toBe('No unsaved changes')
})

test('unsavedLabel with exactly one change is singular', () => {
  expect(unsavedLabel(ONE_FIELD)).toBe('1 unsaved change')
})

test('unsavedLabel with more than one change is plural', () => {
  expect(unsavedLabel(TWO_FIELDS)).toBe('2 unsaved changes')
})
