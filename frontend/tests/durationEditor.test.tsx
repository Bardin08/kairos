import { useState } from 'react'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, test } from 'vitest'

import DurationEditor from '../src/components/DurationEditor'

/**
 * Mirrors DurationEditor's own preset list (`PRESETS = [15, 30, 45, 60]`),
 * which the component keeps private — named here so an assertion reads as
 * "the standard preset", not as a bare 30.
 */
const SHORT_PRESET = 15
const STANDARD_PRESET = 30
const LONGEST_PRESET = 60

/** Mirrors the component's own MIN_MINUTES / MAX_MINUTES, also private. */
const MIN_MINUTES = 1
const MAX_MINUTES = 1440

/** Mirrors the component's own CUSTOM sentinel value on the select. */
const CUSTOM_OPTION_VALUE = 'custom'

/** A whole number inside the bounds but off the preset list — the custom path. */
const CUSTOM_MINUTES = 90

/** Fails Number.isInteger, not the bounds check — the other way into the same sentence. */
const NON_INTEGER_INPUT = '25.5'

/** One past MAX_MINUTES — the bounds check itself. */
const OVER_MAX_INPUT = String(MAX_MINUTES + 1)

const ADD_LABEL = 'Add a duration'
const CUSTOM_FIELD_LABEL = 'Custom duration in minutes'
const ADD_CUSTOM_BUTTON_LABEL = 'Add'

function removeLabel(minutes: number) {
  return `Remove ${minutes} minutes`
}

// A chip and the matching preset <option> both read "N min" — the option is
// excluded by tag rather than by class, since the task forbids class queries
// and a chip is not a select option regardless of what CSS it wears.
function chipText(minutes: number) {
  const pattern = new RegExp(`${minutes} min`)
  return (content: string, element: Element | null) =>
    element?.tagName !== 'OPTION' && pattern.test(content)
}

// The component's own refusal wording (frontend/src/components/DurationEditor.tsx),
// quoted exactly rather than paraphrased.
const duplicateMessage = (minutes: number) => `This link already offers ${minutes} minutes.`
const LAST_ONE_MESSAGE = 'A link must offer at least one duration.'
const BOUNDS_MESSAGE = `A duration is a whole number of minutes between ${MIN_MINUTES} and ${MAX_MINUTES}.`

/**
 * The component plus the durations it last emitted, since what leaves it is
 * the thing under test.
 */
function Harness({ start }: { start: number[] }) {
  const [value, setValue] = useState<number[]>(start)

  return (
    <>
      <DurationEditor durations={value} onChange={setValue} />
      <output>{JSON.stringify(value)}</output>
    </>
  )
}

function select() {
  return screen.getByLabelText(ADD_LABEL) as HTMLSelectElement
}

function customField() {
  return screen.getByLabelText(CUSTOM_FIELD_LABEL) as HTMLInputElement
}

function sent(): number[] {
  return JSON.parse(screen.getByRole('status').textContent ?? '[]')
}

function pickPreset(minutes: number) {
  fireEvent.change(select(), { target: { value: String(minutes) } })
}

function openCustom() {
  fireEvent.change(select(), { target: { value: CUSTOM_OPTION_VALUE } })
}

function submitCustom(input: string) {
  fireEvent.change(customField(), { target: { value: input } })
  fireEvent.click(screen.getByRole('button', { name: ADD_CUSTOM_BUTTON_LABEL }))
}

afterEach(cleanup)

test('choosing a preset adds it as a chip', () => {
  render(<Harness start={[STANDARD_PRESET]} />)

  pickPreset(LONGEST_PRESET)

  expect(sent()).toEqual([STANDARD_PRESET, LONGEST_PRESET])
  expect(screen.getByText(chipText(LONGEST_PRESET))).toBeTruthy()
})

test('adding a duration the link already offers is refused, and says so', () => {
  render(<Harness start={[STANDARD_PRESET]} />)

  pickPreset(STANDARD_PRESET)

  expect(screen.getByRole('alert').textContent).toContain(duplicateMessage(STANDARD_PRESET))
  expect(sent()).toEqual([STANDARD_PRESET])
})

test('the last remaining duration cannot be removed', () => {
  render(<Harness start={[STANDARD_PRESET]} />)

  fireEvent.click(screen.getByRole('button', { name: removeLabel(STANDARD_PRESET) }))

  expect(screen.getByRole('alert').textContent).toContain(LAST_ONE_MESSAGE)
  expect(sent()).toEqual([STANDARD_PRESET])
  expect(screen.getByText(chipText(STANDARD_PRESET))).toBeTruthy()
})

test('removing one of several durations leaves the rest, in order', () => {
  render(<Harness start={[SHORT_PRESET, STANDARD_PRESET, LONGEST_PRESET]} />)

  fireEvent.click(screen.getByRole('button', { name: removeLabel(STANDARD_PRESET) }))

  expect(sent()).toEqual([SHORT_PRESET, LONGEST_PRESET])
})

test('a shorter duration added after a longer one keeps insertion order, not ascending order', () => {
  render(<Harness start={[LONGEST_PRESET]} />)

  pickPreset(SHORT_PRESET)

  // This array becomes SortOrder and then the booking page's tab order — a
  // sort here would look harmless and be wrong.
  expect(sent()).toEqual([LONGEST_PRESET, SHORT_PRESET])
})

test('a custom value outside the bounds is refused, whether non-integer or past the maximum', () => {
  render(<Harness start={[STANDARD_PRESET]} />)

  openCustom()
  submitCustom(NON_INTEGER_INPUT)

  expect(screen.getByRole('alert').textContent).toContain(BOUNDS_MESSAGE)
  expect(sent()).toEqual([STANDARD_PRESET])

  submitCustom(OVER_MAX_INPUT)

  expect(screen.getByRole('alert').textContent).toContain(BOUNDS_MESSAGE)
  expect(sent()).toEqual([STANDARD_PRESET])
})

test('a valid custom value is accepted, and the custom field disappears', () => {
  render(<Harness start={[STANDARD_PRESET]} />)

  openCustom()
  submitCustom(String(CUSTOM_MINUTES))

  expect(sent()).toEqual([STANDARD_PRESET, CUSTOM_MINUTES])
  expect(screen.queryByLabelText(CUSTOM_FIELD_LABEL)).toBeNull()
})

test('the placeholder option emits nothing', () => {
  render(<Harness start={[STANDARD_PRESET]} />)

  fireEvent.change(select(), { target: { value: '' } })

  expect(sent()).toEqual([STANDARD_PRESET])
})
