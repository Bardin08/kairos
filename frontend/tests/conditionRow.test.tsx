import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'

import ConditionRow from '../src/routes/admin/ConditionRow'

const BANNER_ID = 'conditions'

afterEach(cleanup)

test('one problem reads in the singular and says it can be expanded', () => {
  render(<ConditionRow count={1} controls={BANNER_ID} onExpand={() => {}} />)

  const row = screen.getByRole('button')
  expect(row.textContent).toContain('1 problem')
  expect(row.getAttribute('aria-expanded')).toBe('false')
  expect(row.getAttribute('aria-controls')).toBe(BANNER_ID)
})

test('two problems read in the plural', () => {
  render(<ConditionRow count={2} controls={BANNER_ID} onExpand={() => {}} />)

  expect(screen.getByRole('button').textContent).toContain('2 problems')
})

test('pressing it asks for the banner back', () => {
  const onExpand = vi.fn()
  render(<ConditionRow count={1} controls={BANNER_ID} onExpand={onExpand} />)

  fireEvent.click(screen.getByRole('button'))
  expect(onExpand).toHaveBeenCalledTimes(1)
})

test('a count of zero renders nothing', () => {
  const { container } = render(<ConditionRow count={0} controls={BANNER_ID} onExpand={() => {}} />)

  expect(container.querySelector('button')).toBeNull()
})
