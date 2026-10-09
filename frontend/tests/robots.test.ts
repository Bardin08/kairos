import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { expect, test } from 'vitest'

import { LANDING_PATH, PRIVACY_PATH, TERMS_PATH } from '../src/lib/legal'

const robots = readFileSync(resolve(process.cwd(), 'public/robots.txt'), 'utf8')
const app = readFileSync(resolve(process.cwd(), 'src/App.tsx'), 'utf8')

const PUBLIC_PATHS = [LANDING_PATH, PRIVACY_PATH, TERMS_PATH]

function directives(name: string): string[] {
  return robots
    .split('\n')
    .filter((line) => line.startsWith(`${name}:`))
    .map((line) => line.slice(name.length + 1).trim())
}

// Every literal route segment App.tsx declares, read from the source so a route
// added later fails here until robots.txt says what crawlers may do with it.
function routePrefixes(): string[] {
  const paths = [...app.matchAll(/path="(\/[^"]*)"/g)].map((match) => match[1])
  return paths.map((path) => path.split('/:')[0])
}

test('the legal pages stay crawlable, so a reviewer and a search engine can read them', () => {
  const disallowed = directives('Disallow')
  for (const path of PUBLIC_PATHS) {
    expect(disallowed.some((rule) => rule === path || (rule !== '' && path.startsWith(rule) && rule !== LANDING_PATH))).toBe(false)
  }
  expect(disallowed).not.toContain(LANDING_PATH)
})

test('every other route is disallowed', () => {
  const disallowed = directives('Disallow')
  for (const prefix of routePrefixes()) {
    expect(disallowed.some((rule) => prefix.startsWith(rule.replace(/\/$/, '')))).toBe(true)
  }
  expect(disallowed).toContain('/api/')
})
