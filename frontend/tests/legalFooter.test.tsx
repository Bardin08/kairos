import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, expect, test } from 'vitest'

import LegalFooter from '../src/components/LegalFooter'

afterEach(cleanup)

test('links the privacy policy and the terms under the attribution', () => {
  render(<LegalFooter />)
  expect(screen.getByRole('link', { name: 'Privacy' }).getAttribute('href')).toBe('/privacy')
  expect(screen.getByRole('link', { name: 'Terms' }).getAttribute('href')).toBe('/terms')
  expect(screen.getByText(/Scheduled with Kairos/)).toBeDefined()
})

test('leaves the attribution out on an operator surface', () => {
  render(<LegalFooter attribution={false} />)
  expect(screen.queryByText(/Scheduled with Kairos/)).toBeNull()
  expect(screen.getByRole('link', { name: 'Privacy' })).toBeDefined()
})
