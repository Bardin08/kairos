import { beforeEach, expect, test } from 'vitest'
import { applyChoice, readChoice } from '../src/lib/theme'

beforeEach(() => {
  localStorage.clear()
  document.documentElement.removeAttribute('data-theme')
})

test('defaults to system when nothing is stored', () => {
  expect(readChoice()).toBe('system')
})

test('system sets no attribute, so prefers-color-scheme decides', () => {
  applyChoice('dark')
  applyChoice('system')
  expect(document.documentElement.hasAttribute('data-theme')).toBe(false)
  expect(localStorage.getItem('theme')).toBeNull()
})

test('an explicit choice persists and wins in both directions', () => {
  applyChoice('light')
  expect(document.documentElement.getAttribute('data-theme')).toBe('light')
  // Storage is asserted as well as the attribute: the attribute is what paints
  // now, and this is what the pre-paint script in index.html reads next load.
  // Without this line the whole choice can stop surviving a reload in silence.
  expect(localStorage.getItem('theme')).toBe('light')
  applyChoice('dark')
  expect(document.documentElement.getAttribute('data-theme')).toBe('dark')
  expect(localStorage.getItem('theme')).toBe('dark')
})
