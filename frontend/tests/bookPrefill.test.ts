import { expect, test } from 'vitest'

import { applyPrefill, type Details } from '../src/pages/bookPrefill'

// What the operator filled in on the invitee's behalf when the invite was issued.
const INVITED_NAME = 'Ada Lovelace'
const INVITED_EMAIL = 'ada@example.com'
const INVITED_NOTES = 'Bring the engine drawings.'

// What the invitee typed for themselves, which outranks all three of the above.
const TYPED_NAME = 'Ada'

// A stored key this version has no input for. `notes`, `name` and `email` are
// the three the operator may fill today; the prefill bag is deliberately read
// more permissively than it is written, so one more can arrive at any release.
const UNKNOWN_KEY = 'company'
const UNKNOWN_VALUE = 'Analytical Engines Ltd'

const BLANK: Details = { name: '', email: '', notes: '' }
const NOTHING_TOUCHED: ReadonlySet<string> = new Set()

test('fills every field the invite carries', () => {
  const filled = applyPrefill(
    BLANK,
    { name: INVITED_NAME, email: INVITED_EMAIL, notes: INVITED_NOTES },
    NOTHING_TOUCHED,
  )

  expect(filled).toEqual({ name: INVITED_NAME, email: INVITED_EMAIL, notes: INVITED_NOTES })
})

/**
 * A prefill is a PARTIAL booking payload, so a bag naming one field is the
 * ordinary case rather than a broken one. Breaks if a missing key is copied as
 * `undefined`, which puts an uncontrolled input on the page.
 */
test('a field the invite does not carry is left empty and fillable', () => {
  const filled = applyPrefill(BLANK, { email: INVITED_EMAIL }, NOTHING_TOUCHED)

  expect(filled).toEqual({ name: '', email: INVITED_EMAIL, notes: '' })
})

/** An invite with an empty bag is valid — it is a single-use URL and nothing more. */
test('an invite that carries nothing leaves the form exactly as it was', () => {
  expect(applyPrefill(BLANK, {}, NOTHING_TOUCHED)).toEqual(BLANK)
})

/**
 * The race this whole one-shot exists for: the invite read resolves AFTER the
 * invitee has started typing, and must not land on top of their work. Touched
 * rather than non-empty is the rule — an invitee who cleared a field meant to
 * clear it, and refilling it would be the operator overruling them twice.
 */
test('a field the invitee has touched survives the invite arriving late', () => {
  const typed: Details = { name: TYPED_NAME, email: '', notes: '' }

  const filled = applyPrefill(
    typed,
    { name: INVITED_NAME, email: INVITED_EMAIL, notes: INVITED_NOTES },
    new Set(['name', 'email']),
  )

  expect(filled.name).toBe(TYPED_NAME)
  expect(filled.email).toBe('')
  // Untouched, so the operator's guess still stands where the invitee has not been.
  expect(filled.notes).toBe(INVITED_NOTES)
})

/**
 * The server carries a stored key it no longer knows through rather than
 * refusing the invite, so the page is the side that has to ignore one. Breaks if
 * the bag is spread over the details, which puts a key on the state object that
 * no input will ever read and no submit will ever send.
 */
test('a key the form has no input for is ignored', () => {
  const filled = applyPrefill(
    BLANK,
    { name: INVITED_NAME, [UNKNOWN_KEY]: UNKNOWN_VALUE },
    NOTHING_TOUCHED,
  )

  expect(filled).toEqual({ name: INVITED_NAME, email: '', notes: '' })
})
