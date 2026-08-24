import { expect, test } from 'vitest'

import type { AdminLink } from '../src/routes/admin/adminLinks'
import { changedCount, draftOf, unsavedLabel, type EditorDraft } from '../src/routes/admin/editorDraft'

// A link that offers a short call and a long one, overrides its margin, and
// inherits the other three.
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

// sameDurations is a length check followed by an element check. The element check
// alone (`baseline.every((minutes, index) => minutes === draft[index])`) visits
// only the indices the shorter array has, so a baseline that is a strict prefix of
// the draft matches at every index it looks at and nothing notices the extra
// elements. The test above changes both length and contents, so it cannot isolate
// the length check; this one does — delete it and this test fails.
test('a baseline durations array that is a strict prefix of the draft is still a change', () => {
  const prefixBaseline = { ...BASE, durations: [SHORT_CALL] }
  expect(changedCount(prefixBaseline, BASE)).toBe(ONE_FIELD)
})

// The reverse prefix direction, pinned for symmetry with the case above. It does
// not exercise the length check: with the baseline the longer array, `every` walks
// past the end of the draft and compares a real number against `undefined`, which
// fails the element check on its own — verified by deleting the length check from
// a scratch copy. Kept because a truncated draft being a change is real behaviour.
test('a draft durations array that is a strict prefix of the baseline is also a change', () => {
  const shortDraft = { ...BASE, durations: [SHORT_CALL] }
  expect(changedCount(BASE, shortDraft)).toBe(ONE_FIELD)
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

const NINE_FIELDS = 9
const ALL_NINE_NAME = 'A completely different name'
const ALL_NINE_SLUG = 'a-completely-different-slug'
const ALL_NINE_DESCRIPTION = 'A completely different description.'
const ALL_NINE_PATTERN_ID = 'pattern-2'
const ALL_NINE_DURATIONS = [QUARTER_HOUR]
const ALL_NINE_MARGIN = 45
const ALL_NINE_NOTICE = 20
const ALL_NINE_DAILY_CAP = 3
const ALL_NINE_INCREMENT = 10

// One test moving every one of the nine at once: the only way to catch a field
// counted twice, or a tenth field added to EditorDraft and forgotten here.
test('every one of the nine editable values changing counts as nine', () => {
  const draft: EditorDraft = {
    name: ALL_NINE_NAME,
    slug: ALL_NINE_SLUG,
    description: ALL_NINE_DESCRIPTION,
    patternId: ALL_NINE_PATTERN_ID,
    durations: ALL_NINE_DURATIONS,
    limits: {
      marginMinutes: ALL_NINE_MARGIN,
      minNoticeMinutes: ALL_NINE_NOTICE,
      dailyCap: ALL_NINE_DAILY_CAP,
      startIncrementMinutes: ALL_NINE_INCREMENT,
    },
  }

  expect(changedCount(BASE, draft)).toBe(NINE_FIELDS)
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

const NO_PATTERNS_YET_ID = ''

// The state the editor is genuinely in when the operator has created no
// patterns yet: there is no id to pass, so an empty string must pass through
// unchanged rather than being substituted or rejected.
test('draftOf null with no pattern id yet passes the empty string through', () => {
  const draft = draftOf(null, NO_PATTERNS_YET_ID)
  expect(draft.patternId).toBe(NO_PATTERNS_YET_ID)
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

test('unsavedLabel stays plural well past the singular boundary', () => {
  expect(unsavedLabel(NINE_FIELDS)).toBe('9 unsaved changes')
})
