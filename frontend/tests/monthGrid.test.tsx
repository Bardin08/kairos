import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, expect, test } from 'vitest'

import MonthGrid, { type DayState } from '../src/components/MonthGrid'

// August 2026. The 18th is a Tuesday, the 19th a Wednesday.
const MONTH = '2026-08'

const NO_COUNTS = new Map<string, number>()

function day(name: string): HTMLElement {
  return screen.getByRole('button', { name: new RegExp(name) })
}

afterEach(cleanup)

// Breaks if a blocked day is rendered as an ordinary empty one. A count cannot
// carry the difference — a blocked Monday and a Monday the pattern never opens
// both count zero — and telling them apart is the whole reason the editor
// previews a pattern as a month at all.
test('marks a blocked day differently from a day that merely has nothing', () => {
  const states = new Map<string, DayState>([
    ['2026-08-18', 'blocked'],
    ['2026-08-19', 'closed'],
  ])

  render(
    <MonthGrid
      month={MONTH}
      counts={NO_COUNTS}
      states={states}
      selected={null}
      onSelect={() => {}}
    />,
  )

  expect(day('18 August').getAttribute('data-day-state')).toBe('blocked')
  expect(day('19 August').getAttribute('data-day-state')).toBe('closed')
})

// Breaks if the state channel leaks into the booking page, which passes no
// states and whose markup must not move a byte for a feature only the admin
// editor uses.
test('renders exactly as before when no states are given', () => {
  const { container } = render(
    <MonthGrid
      month={MONTH}
      counts={new Map([['2026-08-18', 3]])}
      selected={null}
      onSelect={() => {}}
    />,
  )

  expect(container.querySelectorAll('[data-day-state]')).toHaveLength(0)
  expect(day('18 August').getAttribute('data-state')).toBe('available')
})
