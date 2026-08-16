// The availability editor's ten checks, run against a real instance.
//
// Sibling of measure-ui.mjs and run the same way: by hand, against an instance
// you have seeded and signed into, never from scripts/verify.sh. Where that
// script measures the design system's pixel rules, this one measures what the
// two-pane editor at /admin/availability/{patternId} costs an operator — clicks,
// requests, and whether the month it draws agrees with the slots an invitee is
// actually offered.
//
//   KAIROS_BASE=http://localhost:5301 \
//   KAIROS_COOKIE=<.AspNetCore.Cookies value> \
//   KAIROS_PATTERN=<pattern id> \
//   KAIROS_SLUG=consulting \
//   KAIROS_OUT=/tmp/kairos-availability \
//   node scripts/measure-availability.mjs
//
// The fixture it expects: one pattern open Monday to Friday, one active link
// standing on it with no unusual limits, and the organizer zone the ZONE below
// names. Anything else and the day-versus-slot comparison is measuring the
// fixture rather than the code.
import { createRequire } from 'node:module'
import { mkdirSync, writeFileSync } from 'node:fs'

const require = createRequire(new URL('../frontend/package.json', import.meta.url))
const { chromium } = require('playwright')

const BASE = process.env.KAIROS_BASE ?? 'http://localhost:5301'
const COOKIE = process.env.KAIROS_COOKIE ?? ''
const PATTERN = process.env.KAIROS_PATTERN ?? ''
const SLUG = process.env.KAIROS_SLUG ?? 'consulting'
const OUT = process.env.KAIROS_OUT ?? '/tmp/kairos-availability'
const ZONE = process.env.KAIROS_ZONE ?? 'Europe/Warsaw'

/** The link's only duration in the fixture, and what the preview asks slots for. */
const DURATION = 25

// 1280x800 is the check's own viewport, and it is shorter than the 900 the
// design measurements use: the "without scrolling" claim is about the height a
// laptop actually has, so measuring it at a taller one would prove nothing.
const DESKTOP = { width: 1280, height: 800 }
// One pixel below the 900 breakpoint, which is where a breakpoint is tested.
const STACKED = { width: 899, height: 900 }

/** DESIGN.md's Tap Floor, in CSS pixels. */
const TAP_FLOOR = 44

const EDITOR = `/admin/availability/${PATTERN}`

const results = {}
const record = (name, value) => {
  results[name] = value
  console.log(`\n=== ${name} ===`)
  console.log(JSON.stringify(value, null, 2))
}

mkdirSync(OUT, { recursive: true })

const rectOf = (node) => {
  const r = node.getBoundingClientRect()
  return { x: r.x, y: r.y, width: r.width, height: r.height, bottom: r.bottom }
}
const RECT = `(${rectOf.toString()})`

/**
 * A slots read with no cookie on it at all — a separate fetch rather than the
 * browser's, because a request made from a signed-in context proves nothing
 * about what an invitee can see.
 */
async function anonymousSlots(from, to) {
  const query = new URLSearchParams({ from, to, duration: String(DURATION) })
  const response = await fetch(`${BASE}/api/v1/links/${SLUG}/slots?${query}`)
  if (!response.ok) throw new Error(`slots ${from}..${to} -> ${response.status}`)
  return response.json()
}

const dateInZone = new Intl.DateTimeFormat('en-CA', {
  timeZone: ZONE,
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
})

/** Which organizer date each instant falls on, and how many land on each. */
function countByDay(slots) {
  const counts = new Map()
  for (const slot of slots) {
    const day = dateInZone.format(new Date(slot))
    counts.set(day, (counts.get(day) ?? 0) + 1)
  }
  return counts
}

const MONTH_KEY = 'YYYY-MM'.length
const pad = (value) => String(value).padStart(2, '0')

/** The month after the one today falls in, as `YYYY-MM`. Every day of it is future. */
function nextMonth() {
  const [year, month] = dateInZone.format(new Date()).slice(0, MONTH_KEY).split('-').map(Number)
  const rolled = month === 12 ? { year: year + 1, month: 1 } : { year, month: month + 1 }
  return `${rolled.year}-${pad(rolled.month)}`
}

/** Every date key in `YYYY-MM`, first to last. */
function daysOf(month) {
  const [year, index] = month.split('-').map(Number)
  const days = new Date(Date.UTC(year, index, 0)).getUTCDate()
  return Array.from({ length: days }, (_, at) => `${month}-${pad(at + 1)}`)
}

async function withPage(browser, viewport, body) {
  const context = await browser.newContext({ viewport, timezoneId: ZONE, deviceScaleFactor: 2 })
  if (COOKIE) {
    await context.addCookies([
      { name: '.AspNetCore.Cookies', value: COOKIE, url: BASE, httpOnly: true },
    ])
  }
  const page = await context.newPage()
  try {
    return await body(page)
  } finally {
    await context.close()
  }
}

/** Every write this page sends to the patterns slice, in order. */
function watchWrites(page) {
  const writes = []
  page.on('request', (request) => {
    if (request.method() === 'GET') return
    if (!request.url().includes('/api/v1/admin/patterns')) return
    writes.push(`${request.method()} ${request.url().slice(BASE.length)}`)
  })
  return writes
}

async function editorReady(page, { month = null } = {}) {
  await page.goto(`${BASE}${EDITOR}`, { waitUntil: 'networkidle' })
  await page.waitForSelector('.editor-cols', { timeout: 15000 })
  if (month !== null) {
    await page.click('[aria-label="Next month"]')
    await page.waitForFunction(
      (label) => document.querySelector('.month-label')?.textContent?.includes(label),
      month,
      { timeout: 5000 },
    )
  }
}

/** Every square of the drawn month: its date, what the pattern calls it, whether it is live. */
async function grid(page) {
  return page.$$eval('.days .day[data-day-state]', (nodes) =>
    nodes.map((node) => ({
      day: node.getAttribute('aria-label').split(',')[0],
      dayState: node.getAttribute('data-day-state'),
      state: node.getAttribute('data-state'),
      label: node.getAttribute('aria-label'),
      text: node.textContent.trim(),
    })),
  )
}

const monthName = (month) =>
  new Intl.DateTimeFormat('en-GB', { month: 'long', year: 'numeric', timeZone: 'UTC' }).format(
    new Date(`${month}-01T00:00:00Z`),
  )

const main = async () => {
  if (PATTERN === '') throw new Error('KAIROS_PATTERN is required')

  const month = nextMonth()
  const label = monthName(month)
  const days = daysOf(month)
  const first = days[0]
  const last = days[days.length - 1]

  const browser = await chromium.launch()

  // ---- 2. patternDays and the real slots agree on which days are open.
  //
  // A whole month in the future, so nothing in it is filtered by having already
  // happened — patternDays knows nothing about the clock and would disagree
  // about this morning for a reason that is not drift.
  const baseline = countByDay(await anonymousSlots(first, last))
  const squares = await withPage(browser, DESKTOP, async (page) => {
    await editorReady(page, { month: label })
    return grid(page)
  })

  const openByPattern = squares.filter((s) => s.dayState === 'open').map((s) => s.day)
  const withSlots = days.filter((day) => (baseline.get(day) ?? 0) > 0)
  const dayOf = new Map(squares.map((s, index) => [days[index], s]))

  record('check-2-days-agree', {
    month,
    squares: squares.length,
    daysInMonth: days.length,
    openByPattern: openByPattern.length,
    withSlots: withSlots.length,
    openButNoSlots: days.filter(
      (day) => dayOf.get(day)?.dayState === 'open' && (baseline.get(day) ?? 0) === 0,
    ),
    slotsButNotOpen: days.filter(
      (day) => dayOf.get(day)?.dayState !== 'open' && (baseline.get(day) ?? 0) > 0,
    ),
    closedByPattern: squares.filter((s) => s.dayState === 'closed').length,
    slotsPerOpenDay: [...new Set(withSlots.map((day) => baseline.get(day)))],
  })

  // ---- 6 and 7. The two panes, the link picker, and the tap target.
  const layout = {}
  for (const [name, viewport] of [
    ['1280', DESKTOP],
    ['899', STACKED],
  ]) {
    layout[name] = await withPage(browser, viewport, async (page) => {
      await editorReady(page)
      const panes = await page.$$eval(
        '.editor-cols > section',
        (nodes, rect) => nodes.map((node) => ({ heading: node.querySelector('h2').textContent, ...eval(rect)(node) })),
        RECT,
      )
      const square = await page.$eval('.days .day[data-day-state]', (node, rect) => eval(rect)(node), RECT)
      await page.screenshot({ path: `${OUT}/editor-${name}.png`, fullPage: false })
      return {
        viewport,
        panes,
        sameRow: panes.length === 2 && panes[0].y === panes[1].y,
        sameColumn: panes.length === 2 && panes[0].x === panes[1].x,
        weekFirst: panes[0]?.heading,
        daySquare: { width: square.width, height: square.height, tapFloor: TAP_FLOOR },
      }
    })
  }
  record('check-7-panes', layout)

  record(
    'check-6-links-without-scrolling',
    await withPage(browser, DESKTOP, async (page) => {
      await editorReady(page)
      const picker = await page.$eval('#preview-as', (node, rect) => ({
        options: [...node.options].map((option) => option.textContent),
        ...eval(rect)(node),
      }), RECT)
      await page.selectOption('#preview-as', { label: 'Consulting call' })
      await page.waitForFunction(
        () => !document.body.textContent.includes('Reading the times for'),
        undefined,
        { timeout: 15000 },
      )
      const withCounts = await page.$$eval('.days .day[data-state="available"]', (nodes) =>
        nodes.map((node) => node.getAttribute('aria-label')),
      )
      const grid = await page.$eval('.days', (node, rect) => eval(rect)(node), RECT)
      // `.admin-shell` is exactly one viewport tall and `.admin-main` is what
      // scrolls inside it, so "without scrolling" is a question about that
      // element's box and never about the document's.
      const scroll = await page.evaluate((rect) => {
        const main = document.querySelector('.admin-main')
        return {
          documentScrollTop: document.scrollingElement.scrollTop,
          mainScrollTop: main.scrollTop,
          mainScrollHeight: main.scrollHeight,
          mainClientHeight: main.clientHeight,
          mainRect: eval(rect)(main),
          innerHeight: window.innerHeight,
        }
      }, RECT)
      await page.screenshot({ path: `${OUT}/editor-links.png`, fullPage: false })
      return {
        picker,
        firstThreeCounted: withCounts.slice(0, 3),
        countedDays: withCounts.length,
        gridBottom: grid.bottom,
        pickerBottom: picker.bottom,
        scroll,
        pickerVisibleUnscrolled: picker.bottom <= scroll.mainRect.bottom,
        gridVisibleUnscrolled: grid.bottom <= scroll.mainRect.bottom,
        panePastTheFold: scroll.mainScrollHeight - scroll.mainClientHeight,
      }
    }),
  )

  // ---- 3 and 1. One click blocks a date, and the anonymous read loses that day.
  //
  // The target is the first weekday of the month the pattern opens, which the
  // baseline above has already been read for.
  const target = withSlots[0]
  const before = baseline.get(target) ?? 0
  const neighbour = withSlots[1]

  const blocking = await withPage(browser, DESKTOP, async (page) => {
    await editorReady(page, { month: label })
    const writes = watchWrites(page)
    const square = page.locator(`.days .day[data-day-state]`).nth(Number(target.slice(-2)) - 1)
    const labelBefore = await square.getAttribute('aria-label')
    await square.click()
    await page.waitForFunction(
      (date) =>
        document
          .querySelector(`.days .day[aria-label*="${date}"]`)
          ?.getAttribute('data-day-state') === 'blocked',
      // The square's own accessible name carries the long date, which is the
      // only handle on it that is not an index into the grid.
      labelBefore.split(',')[0],
      { timeout: 10000 },
    )
    const labelAfter = await square.getAttribute('aria-label')
    return { clicks: 1, writes, labelBefore, labelAfter }
  })

  const after = countByDay(await anonymousSlots(first, last))
  record('check-1-and-3-block-a-date', {
    target,
    neighbour,
    clicksToBlock: blocking.clicks,
    writes: blocking.writes,
    squareBefore: blocking.labelBefore,
    squareAfter: blocking.labelAfter,
    anonymousSlotsOnTarget: { before, after: after.get(target) ?? 0 },
    anonymousSlotsOnNeighbour: {
      before: baseline.get(neighbour) ?? 0,
      after: after.get(neighbour) ?? 0,
    },
    monthTotal: { before: [...baseline.values()].reduce((a, b) => a + b, 0), after: [...after.values()].reduce((a, b) => a + b, 0) },
  })

  // ---- 4 and 5. Shifting the week, and opening and closing a day.
  record(
    'check-4-and-5-week',
    await withPage(browser, DESKTOP, async (page) => {
      await editorReady(page)
      const writes = watchWrites(page)

      const rowsBefore = await page.$$eval('.week-day', (nodes) =>
        nodes.map((node) => node.textContent.replace(/\s+/g, ' ').trim()),
      )
      const valuesBefore = await page.$$eval('.week-time', (nodes) => nodes.map((n) => n.value))

      await page.fill('[aria-label="Monday, window 1, opens at"]', '10:00')
      await page.fill('[aria-label="Monday, window 1, closes at"]', '18:00')
      const writesAfterTyping = writes.length

      await page.click('[aria-label="Monday: Use these hours on every open day"]')
      const writesAfterApply = writes.length
      const valuesAfterApply = await page.$$eval('.week-time', (nodes) => nodes.map((n) => n.value))

      await page.click('.btn-primary:has-text("Save weekly hours")')
      await page.waitForFunction(
        () => !document.body.textContent.includes('Not saved yet'),
        undefined,
        { timeout: 10000 },
      )
      const writesAfterSave = writes.slice()

      // 5. Closing a day is removing its last window; opening one is the button
      // the emptied row draws in its place.
      const closingWrites = writes.length
      await page.click('[aria-label="Remove Friday window 1"]')
      const fridayAfterClose = await page.$eval('.week-day:nth-child(5)', (node) =>
        node.textContent.replace(/\s+/g, ' ').trim(),
      )
      await page.click('[aria-label="Open Saturday"]')
      const saturdayAfterOpen = await page.$$eval(
        '.week-day:nth-child(6) .week-time',
        (nodes) => nodes.map((n) => ({ label: n.getAttribute('aria-label'), value: n.value })),
      )
      const writesFromOpenAndClose = writes.length - closingWrites

      return {
        rowsBefore,
        valuesBefore,
        valuesAfterApply,
        fieldEditsToShift: 2,
        pressesToShift: 2,
        writesAfterTyping,
        writesAfterApply,
        writesTotalForShift: writesAfterSave,
        clicksToCloseADay: 1,
        clicksToOpenADay: 1,
        writesFromOpenAndClose,
        fridayAfterClose,
        saturdayAfterOpen,
      }
    }),
  )

  // ---- 8. Signed out, the editor is the locked screen and nothing else.
  const context = await browser.newContext({ viewport: DESKTOP, timezoneId: ZONE })
  const page = await context.newPage()
  await page.goto(`${BASE}${EDITOR}`, { waitUntil: 'networkidle' })
  await page.waitForTimeout(1000)
  record('check-8-locked', {
    url: page.url(),
    heading: await page.$eval('h1', (node) => node.textContent).catch(() => null),
    shell: await page.$$eval('.admin-shell', (nodes) => nodes.length),
    rail: await page.$$eval('.rail', (nodes) => nodes.length),
    tabbar: await page.$$eval('.tabbar', (nodes) => nodes.length),
    editorCols: await page.$$eval('.editor-cols', (nodes) => nodes.length),
    monthGrid: await page.$$eval('.days', (nodes) => nodes.length),
    buttons: await page.$$eval('button, a.btn', (nodes) => nodes.map((n) => n.textContent.trim())),
    bodyText: await page.$eval('body', (node) => node.textContent.replace(/\s+/g, ' ').trim()),
  })
  await page.screenshot({ path: `${OUT}/editor-locked.png` })
  await context.close()

  await browser.close()
  writeFileSync(`${OUT}/results.json`, JSON.stringify(results, null, 2))
}

await main()
