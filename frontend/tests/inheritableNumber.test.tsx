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

const FIELD = 'Margin between meetings'
const OVERRIDE = `Override ${FIELD}`
const INHERIT = `Use global ${FIELD}`
const INHERITED = 'inherit'

/**
 * The component plus the value it last emitted, since what leaves it is the
 * thing under test. `inherit` and `0` are printed differently on purpose: a
 * harness that stringified null as "" would hide exactly the confusion these
 * tests exist to catch.
 */
function Harness({ start }: { start: number | null }) {
  const [value, setValue] = useState<number | null>(start)

  return (
    <>
      <InheritableNumber
        label={FIELD}
        short="min"
        value={value}
        globalValue={GLOBAL}
        min={NO_FLOOR}
        onChange={setValue}
      />
      <output>{value === null ? INHERITED : String(value)}</output>
    </>
  )
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
