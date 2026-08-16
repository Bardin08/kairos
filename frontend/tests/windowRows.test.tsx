import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'

import WindowRows from '../src/routes/admin/WindowRows'

const LABEL = 'Monday'
const MORNING = { start: '09:00', end: '12:00' }
const AFTERNOON = { start: '13:00', end: '18:00' }

afterEach(cleanup)

test('each row offers a remove named for the day and the window it removes', () => {
  render(<WindowRows label={LABEL} windows={[MORNING, AFTERNOON]} onChange={vi.fn()} />)

  // The visible mark is an ×, so the name is the only thing telling two rows
  // apart — and the week and a selected date are on one page together.
  expect(screen.getByRole('button', { name: `Remove ${LABEL} window 1` })).toBeTruthy()
  expect(screen.getByRole('button', { name: `Remove ${LABEL} window 2` })).toBeTruthy()
})

test('removing a row hands back the set without it, and keeps the order', () => {
  const onChange = vi.fn()
  render(<WindowRows label={LABEL} windows={[MORNING, AFTERNOON]} onChange={onChange} />)

  fireEvent.click(screen.getByRole('button', { name: `Remove ${LABEL} window 1` }))

  expect(onChange).toHaveBeenCalledWith([AFTERNOON])
})
