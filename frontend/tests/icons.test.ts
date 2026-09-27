import { existsSync, readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { expect, test } from 'vitest'

// Resolved from the working directory, as stylesheet.test.ts explains.
const indexHtml = resolve(process.cwd(), 'index.html')
const publicDir = resolve(process.cwd(), 'public')

const ICON_LINK = /<link rel="(?:icon|apple-touch-icon)" href="\/([^"]+)"/g

// The favicon, its PNG fallback and the touch icon.
const EXPECTED_ICONS = 3

// A browser asks for a missing icon, gets the SPA fallback's index.html, and
// shows nothing — no error anywhere says the link points at a file that ships.
test('every icon index.html links to ships in public/', () => {
  const html = readFileSync(indexHtml, 'utf8')
  const hrefs = [...html.matchAll(ICON_LINK)].map((match) => match[1])

  expect(hrefs).toHaveLength(EXPECTED_ICONS)
  for (const href of hrefs) expect(existsSync(resolve(publicDir, href)), href).toBe(true)
})
