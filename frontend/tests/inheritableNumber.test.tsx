import { useState } from 'react'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, test } from 'vitest'

import InheritableNumber from '../src/components/InheritableNumber'

// The seeded global margin. The number matters: an inherited 15 and an
// overridden 15 are the same digits on screen, which is the whole reason this
// control exists.
const GLOBAL = 15

const CHECKBOX = 'Use the global default — 15 minutes'
const FIELD = 'Margin between meetings'

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
        unit="minutes"
        value={value}
        globalValue={GLOBAL}
        min={0}
        onChange={setValue}
      />
      <output>{value === null ? 'inherit' : String(value)}</output>
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

test('while inherited the box shows the global, greyed out, and null is what leaves', () => {
  render(<Harness start={null} />)

  // Not an empty box. An operator seeing "15" they cannot type into understands
  // it is the global; an operator seeing nothing wonders whether the setting is
  // missing, and the first thing they do about it is type 15 — which is the
  // default-copying bug, entered by hand.
  expect(box().value).toBe(String(GLOBAL))
  expect(box().disabled).toBe(true)
  expect(sent()).toBe('inherit')
})

test('an explicit zero leaves as zero rather than as inherit', () => {
  render(<Harness start={null} />)

  fireEvent.click(screen.getByLabelText(CHECKBOX))
  fireEvent.change(box(), { target: { value: '0' } })

  // The state a two-state control cannot hold apart from "inherit", and the one
  // Part A's design turns on: zero margin is no gap between meetings, and a link
  // that means it must not be read as a link that never said anything.
  expect(box().disabled).toBe(false)
  expect(sent()).toBe('0')
})

test('inheriting again after an explicit zero goes back to null, not to 0', () => {
  render(<Harness start={null} />)

  fireEvent.click(screen.getByLabelText(CHECKBOX))
  fireEvent.change(box(), { target: { value: '0' } })
  fireEvent.click(screen.getByLabelText(CHECKBOX))

  // The round trip closes: three states reachable through two controls, in both
  // directions. A component that kept the 0 and only disabled the input would
  // pass the first two tests and still write 0 to a link that asked to inherit.
  expect(sent()).toBe('inherit')
  expect(box().value).toBe(String(GLOBAL))
  expect(box().disabled).toBe(true)
})
