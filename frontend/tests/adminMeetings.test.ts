import { afterEach, expect, test, vi } from 'vitest'

import { resyncMeetings } from '../src/routes/admin/adminMeetings'

const LINK_ID = '0199a1f0-0000-7000-8000-00000000000a'
const ATTEMPTED = 2

function stubFetch() {
  const spy = vi.fn(() =>
    Promise.resolve({
      ok: true,
      status: 200,
      headers: new Headers(),
      json: () => Promise.resolve({ attempted: ATTEMPTED }),
    } as unknown as Response),
  )
  vi.stubGlobal('fetch', spy)
  return spy
}

function sent(spy: ReturnType<typeof stubFetch>): { url: string; method: string } {
  const [input, init] = spy.mock.calls[0] as unknown as [RequestInfo | URL, RequestInit]
  return { url: String(input), method: String(init.method) }
}

afterEach(() => {
  vi.unstubAllGlobals()
})

// The press is scoped to the link filter, and the scope travels only in the
// query string: a resync that dropped it would place every link's meetings.
test('a resync for one link names it in the query and answers how many were attempted', async () => {
  const spy = stubFetch()

  const attempted = await resyncMeetings(LINK_ID)

  expect(sent(spy)).toEqual({
    url: `/api/v1/admin/meetings/resync?linkId=${LINK_ID}`,
    method: 'POST',
  })
  expect(attempted).toBe(ATTEMPTED)
})

test('a resync for every link carries no query', async () => {
  const spy = stubFetch()

  await resyncMeetings(undefined)

  expect(sent(spy)).toEqual({ url: '/api/v1/admin/meetings/resync', method: 'POST' })
})
