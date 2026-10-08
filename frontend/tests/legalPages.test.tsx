import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { Router } from 'wouter'
import { memoryLocation } from 'wouter/memory-location'

import App from '../src/App'
import { FALLBACK_JURISDICTION, FALLBACK_OPERATOR, LIMITED_USE } from '../src/routes/legal/legalDetails'

const OPERATOR = 'Ada Operator'
const CONTACT = 'privacy@kairos.test'
const JURISDICTION = 'Poland'

const CONFIGURED = { operatorName: OPERATOR, contactEmail: CONTACT, jurisdiction: JURISDICTION }
const UNSET = { operatorName: null, contactEmail: null, jurisdiction: null }

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

function answer(body: unknown, ok = true) {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok, json: async () => body }))
}

function renderAt(path: string) {
  const { hook } = memoryLocation({ path })
  render(
    <Router hook={hook}>
      <App />
    </Router>,
  )
}

test('the landing page names the operator and links both texts', async () => {
  answer(CONFIGURED)
  renderAt('/')
  expect((await screen.findAllByText(new RegExp(OPERATOR))).length).toBeGreaterThan(0)
  expect(screen.getAllByRole('link', { name: /privacy/i })[0].getAttribute('href')).toBe('/privacy')
  expect(screen.getAllByRole('link', { name: /terms/i })[0].getAttribute('href')).toBe('/terms')
})

test('the privacy policy carries the contact and the Limited Use statement', async () => {
  answer(CONFIGURED)
  renderAt('/privacy')
  const contact = await screen.findAllByRole('link', { name: CONTACT })
  expect(contact[0].getAttribute('href')).toBe(`mailto:${CONTACT}`)
  expect(screen.getByText(LIMITED_USE)).toBeDefined()
})

test('the privacy policy reads complete when the details are unset', async () => {
  answer(UNSET)
  renderAt('/privacy')
  expect(await screen.findByRole('heading', { name: 'Privacy policy' })).toBeDefined()
  expect(screen.getAllByText(new RegExp(FALLBACK_OPERATOR, 'i')).length).toBeGreaterThan(0)
  expect(screen.queryByRole('link', { name: CONTACT })).toBeNull()
})

test('the terms fall back when the details cannot be read', async () => {
  answer({}, false)
  renderAt('/terms')
  expect(await screen.findByRole('heading', { name: 'Terms of service' })).toBeDefined()
  expect(screen.getByText(new RegExp(FALLBACK_JURISDICTION))).toBeDefined()
})

test('the terms name the governing law', async () => {
  answer(CONFIGURED)
  renderAt('/terms')
  expect(await screen.findByText(new RegExp(`laws of ${JURISDICTION}`))).toBeDefined()
})

test('an unknown path says so and still links the legal pages', () => {
  answer(UNSET)
  renderAt('/nowhere')
  expect(screen.getByText(/does not lead anywhere/i)).toBeDefined()
  expect(screen.getByRole('link', { name: 'Privacy' })).toBeDefined()
})
