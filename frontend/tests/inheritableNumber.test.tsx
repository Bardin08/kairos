import { useState } from 'react'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, test } from 'vitest'

import InheritableNumber from '../src/components/InheritableNumber'

// The seeded global margin. The number matters: an inherited 15 and an
// overridden 15 are the same digits on screen, which is the whole reason this
// control exists.
const GLOBAL = 15

/** Margin's floor. Zero is a real margin — no gap between two meetings. */
const NO_FLOOR = 0

/** The value that a two-state control cannot tell apart from "inherit". */
const EXPLICIT_ZERO = '0'

/** An ordinary value, distinct from the zero and the global every other test uses. */
const TYPED_VALUE = 42

/** The start increment's real floor, used to exercise the clamp on override. */
const FLOOR_ABOVE_GLOBAL = 5

/** A global that sits below `FLOOR_ABOVE_GLOBAL` — the case the clamp exists for. */
const GLOBAL_BELOW_FLOOR = 0

const HELP = 'Kept clear on both sides of every booking.'

/** For the one test that renders the component directly and never checks what it emits. */
function noop() {
  // Intentionally empty — see the doc comment above.
}

const FIELD = 'Margin between meetings'
const OVERRIDE = `Override ${FIELD}`
const INHERIT = `Use global ${FIELD}`
const INHERITED = 'inherit'

/**
 * The component plus the value it last emitted, since what leaves it is the
 * thing under test. `inherit` and `0` are printed differently on purpose: a
 * harness that stringified null as "" would hide exactly the confusion these
 * tests exist to catch.
 *
 * `min` and `globalValue` default to the values every original test used, so
 * this stays a no-op change for them; the clamp test is the only caller that
 * overrides either.
 */
function Harness({
  start,
  min = NO_FLOOR,
  globalValue = GLOBAL,
}: {
  start: number | null
  min?: number
  globalValue?: number
}) {
  const [value, setValue] = useState<number | null>(start)

  return (
    <>
      <InheritableNumber
        label={FIELD}
        short="min"
        value={value}
        globalValue={globalValue}
        min={min}
        onChange={setValue}
      />
      <output>{value === null ? INHERITED : String(value)}</output>
    </>
  )
}

/**
 * The text of every element the Override button's `aria-describedby` points
 * at, joined the way assistive tech reads a multi-id description: in order,
 * space-separated. `toHaveAccessibleDescription` needs jest-dom, which this
 * project does not have, so the wiring is asserted directly instead.
 */
function overrideDescription() {
  const ids = screen.getByRole('button', { name: OVERRIDE }).getAttribute('aria-describedby') ?? ''
  return ids
    .split(' ')
    .map((elementId) => document.getElementById(elementId)?.textContent ?? '')
    .join(' ')
}

function box() {
  return screen.getByLabelText(FIELD) as HTMLInputElement
}

function sent() {
  return screen.getByRole('status').textContent
}

afterEach(cleanup)

test('while inherited the global is stated as a fact and null is what leaves', () => {
  render(<Harness start={null} />)

  // Not a disabled input showing a number nobody typed. The value is read-only
  // prose with the word Global beside it, so the state is legible without
  // colour and without having to try the field to discover it is dead.
  expect(screen.queryByLabelText(FIELD)).toBeNull()
  expect(screen.getByText('Global')).toBeTruthy()
  expect(screen.getByText(String(GLOBAL), { exact: false })).toBeTruthy()
  expect(sent()).toBe(INHERITED)
})

test('overriding seeds the field with the global rather than with an empty box', () => {
  render(<Harness start={null} />)

  fireEvent.click(screen.getByRole('button', { name: OVERRIDE }))

  // The value the link is on right now is the honest starting point for
  // editing it, and an empty box is indistinguishable from one nobody filled in.
  expect(box().value).toBe(String(GLOBAL))
  expect(sent()).toBe(String(GLOBAL))
})

test('an explicit zero leaves as zero rather than as inherit', () => {
  render(<Harness start={null} />)

  fireEvent.click(screen.getByRole('button', { name: OVERRIDE }))
  fireEvent.change(box(), { target: { value: EXPLICIT_ZERO } })

  // The state a two-state control cannot hold apart from "inherit", and the one
  // the whole limits design turns on: zero margin is no gap between meetings,
  // and a link that means it must not read as a link that never said anything.
  expect(sent()).toBe(EXPLICIT_ZERO)
})

test('inheriting again after an explicit zero goes back to null, not to 0', () => {
  render(<Harness start={null} />)

  fireEvent.click(screen.getByRole('button', { name: OVERRIDE }))
  fireEvent.change(box(), { target: { value: EXPLICIT_ZERO } })
  fireEvent.click(screen.getByRole('button', { name: INHERIT }))

  // The round trip closes: three states reachable through two controls, in both
  // directions. A component that kept the 0 and only hid the input would pass
  // the first three tests and still write 0 to a link that asked to inherit.
  expect(sent()).toBe(INHERITED)
  expect(screen.queryByLabelText(FIELD)).toBeNull()
})

test('pressing override moves focus into the field it just created', () => {
  render(<Harness start={null} />)

  fireEvent.click(screen.getByRole('button', { name: OVERRIDE }))

  // A button that replaces itself with a field and drops focus to the body
  // sends a keyboard user back to the top of the form to find where they were.
  expect(document.activeElement).toBe(box())
})

test('pressing use global returns focus to the button that replaced the field', () => {
  render(<Harness start={null} />)

  fireEvent.click(screen.getByRole('button', { name: OVERRIDE }))
  fireEvent.click(screen.getByRole('button', { name: INHERIT }))

  expect(document.activeElement).toBe(screen.getByRole('button', { name: OVERRIDE }))
})

test("the override button's description states the global and that it is global", () => {
  render(<Harness start={null} />)

  // A screen-reader user who navigates by interactive element skips the
  // static prose entirely and lands straight on this button. Without the
  // description they hear only "Override Margin between meetings" and never
  // learn what they are about to override.
  expect(overrideDescription()).toContain(String(GLOBAL))
  expect(overrideDescription()).toContain('Global')
})

test("overriding clamps a global that sits below this link's floor", () => {
  render(<Harness start={null} min={FLOOR_ABOVE_GLOBAL} globalValue={GLOBAL_BELOW_FLOOR} />)

  fireEvent.click(screen.getByRole('button', { name: OVERRIDE }))

  // The global and this link's floor are set on two different endpoints and
  // can disagree. Seeding with the bare global here would leave the box below
  // its own floor, and the PUT that carried it would come back as an opaque
  // 400 about a number the operator never typed.
  expect(box().value).toBe(String(FLOOR_ABOVE_GLOBAL))
  expect(sent()).toBe(String(FLOOR_ABOVE_GLOBAL))
})

test('typing an ordinary value leaves the component exactly as typed', () => {
  render(<Harness start={null} />)

  fireEvent.click(screen.getByRole('button', { name: OVERRIDE }))
  fireEvent.change(box(), { target: { value: String(TYPED_VALUE) } })

  // Every other test in this file types either nothing or the zero the
  // component treats specially. This is the ordinary path: an ordinary number
  // goes in and the same number comes out unchanged.
  expect(sent()).toBe(String(TYPED_VALUE))
})

test('help renders under the label when the caller passes one', () => {
  // `help` is optional and Harness never passes it, so this is the only test
  // that renders the component directly rather than through the harness.
  render(
    <InheritableNumber
      label={FIELD}
      short="min"
      help={HELP}
      value={null}
      globalValue={GLOBAL}
      min={NO_FLOOR}
      onChange={noop}
    />,
  )

  expect(screen.getByText(HELP)).toBeTruthy()
})
