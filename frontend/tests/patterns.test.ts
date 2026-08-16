import { afterEach, expect, test, vi } from 'vitest'

import { savePatternOverride } from '../src/routes/admin/patterns'

const PATTERN_ID = 'b0a5f0e2-0000-7000-8000-000000000001'
const CHRISTMAS = '2026-12-25'

// Minutes from local midnight, which is what a window on the wire holds.
const MINUTES_PER_HOUR = 60
const NINE_AM = 9 * MINUTES_PER_HOUR
const NOON = 12 * MINUTES_PER_HOUR

/** Answers everything with an empty pattern: nothing here reads the response. */
function stubFetch() {
  const spy = vi.fn(() =>
    Promise.resolve({
      ok: true,
      status: 200,
      headers: new Headers(),
      json: () => Promise.resolve({}),
    } as unknown as Response),
  )
  vi.stubGlobal('fetch', spy)
  return spy
}

/** The body of the one call made, as the server would have received it. */
function sent(spy: ReturnType<typeof stubFetch>): { url: string; method: string; body: unknown } {
  const [input, init] = spy.mock.calls[0] as unknown as [RequestInfo | URL, RequestInit]
  return {
    url: String(input),
    method: String(init.method),
    body: JSON.parse(String(init.body)) as unknown,
  }
}

afterEach(() => {
  vi.unstubAllGlobals()
})

/**
 * The one assertion in this suite that reads what actually goes on the wire.
 *
 * When a date stopped holding one window and started holding a set, this module
 * kept sending `{ startMinute, endMinute }` and the server answered 400 to every
 * save — with `tsc`, eslint and the whole of vitest green, because the body
 * leaves through `JSON.stringify` and the answer comes back into a hand-written
 * type, so nothing between the two ever compared them to the contract. Pinning
 * the body is the cheap half of that: it is the shape a backend change breaks,
 * and it breaks here rather than in the operator's browser.
 *
 * Both meanings of the route are pinned, because they differ only in the body:
 * windows in the list are the hours the date offers, and an empty list blocks
 * it. Nothing about the RESPONSE is pinned — it is parsed by assertion and not
 * by validation, so a test of it would only be asserting its own mock.
 */
test('a date override goes out as its whole set of windows', async () => {
  const hours = stubFetch()
  await savePatternOverride(PATTERN_ID, CHRISTMAS, [
    { startMinute: NINE_AM, endMinute: NOON },
  ])

  expect(sent(hours)).toEqual({
    url: `/api/v1/admin/patterns/${PATTERN_ID}/overrides/${CHRISTMAS}`,
    method: 'PUT',
    body: { windows: [{ startMinute: NINE_AM, endMinute: NOON }] },
  })

  const blocked = stubFetch()
  await savePatternOverride(PATTERN_ID, CHRISTMAS, [])

  expect(sent(blocked).body).toEqual({ windows: [] })
})
