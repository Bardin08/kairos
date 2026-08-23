// The checks that need a layout engine, a real viewport and a screenshot.
//
// jsdom has none of those: it parses markup and computes nothing, so every
// condition in docs/ui/DESIGN.md about a breakpoint, a pixel, a focus ring or a
// monochrome reading was unrunnable in the unit suite and went unrun through
// Parts A, B and C's implementation tasks. This script is what makes them
// runnable. It is not part of scripts/verify.sh and never will be: it needs a
// seeded instance, an operator session and a browser download, none of which
// belong in a gate that runs on every commit.
//
// Run it by hand, against an instance you have seeded:
//
//   KAIROS_BASE=http://127.0.0.1:5300 \
//   KAIROS_COOKIE=<.AspNetCore.Cookies value> \
//   KAIROS_OUT=/tmp/kairos-ui \
//   node scripts/measure-ui.mjs
//
// Playwright is a devDependency of frontend/ rather than of the repository root,
// because frontend/ is the only place with a package.json and adding a second
// one to carry a single tool is worse than the require() below.
import { createRequire } from 'node:module'
import { mkdirSync, writeFileSync } from 'node:fs'

const require = createRequire(new URL('../frontend/package.json', import.meta.url))
const { chromium } = require('playwright')

const BASE = process.env.KAIROS_BASE ?? 'http://127.0.0.1:5300'
const COOKIE = process.env.KAIROS_COOKIE ?? ''
const OUT = process.env.KAIROS_OUT ?? '/tmp/kairos-ui'
const ZONE = process.env.KAIROS_ZONE ?? 'Europe/Warsaw'

// The four viewports the design system names, plus the phone the Thumb Rule is
// written against. 440x956 is the plan's phone; 899 and 759 are one pixel below
// the 900 and 760 breakpoints, which is where a breakpoint is actually tested.
const PHONE = { width: 440, height: 956 }
const DESKTOP = { width: 1280, height: 900 }
const TABBAR = { width: 899, height: 900 }
const RECORDS = { width: 759, height: 900 }

// Neither of these is a design-system breakpoint — nothing in DESIGN.md names
// them and no CSS rule fires at them. WIDE exists because the defect that
// started this branch (a two-digit minute value in a ~1900px box) was first
// seen in a screenshot taken near this width, so a regression there is caught
// where it was originally found. LAPTOP exists because it is the machine the
// operator actually runs the admin surface from day to day — a narrow-column
// check that only ever ran at DESKTOP's 1280 would never see their real window.
const WIDE = { width: 2560, height: 1400 }
const LAPTOP = { width: 1440, height: 900 }

const results = {}
const record = (name, value) => {
  results[name] = value
  console.log(`\n=== ${name} ===`)
  console.log(JSON.stringify(value, null, 2))
}

mkdirSync(OUT, { recursive: true })

// Everything an element needs to be judged by: what it paints, what it is, and
// where it sits. Serialised in the page so one round trip answers a whole check.
const DESCRIBE = `(element) => {
  const style = getComputedStyle(element)
  const rect = element.getBoundingClientRect()
  return {
    tag: element.tagName.toLowerCase(),
    className: element.getAttribute('class'),
    text: (element.textContent ?? '').trim().slice(0, 60),
    color: style.color,
    background: style.backgroundColor,
    borderColor: style.borderTopColor,
    outline: style.outline,
    outlineOffset: style.outlineOffset,
    boxShadow: style.boxShadow,
    fontVariantNumeric: style.fontVariantNumeric,
    fontWeight: style.fontWeight,
    rect: { x: rect.x, y: rect.y, width: rect.width, height: rect.height },
  }
}`

async function withPage(browser, { viewport, colorScheme = 'light', theme = null }, body) {
  const context = await browser.newContext({
    // The harness injects its own probe stylesheets — env() cannot be read from
    // script, and the grayscale pass needs a filter — and `style-src 'self'`
    // blocks both since the security headers shipped. Without this the script
    // dies on its first addStyleTag against any real instance, which is to say
    // the tool that enforces these rules could no longer be run. It relaxes the
    // policy for the measuring browser only; the header the application sends is
    // exactly what SecurityHeadersTests pins, and is what a real browser gets.
    bypassCSP: true,
    viewport,
    colorScheme,
    timezoneId: ZONE,
    deviceScaleFactor: 2,
  })
  if (COOKIE) {
    await context.addCookies([
      { name: '.AspNetCore.Cookies', value: COOKIE, url: BASE, httpOnly: true },
    ])
  }
  if (theme) {
    await context.addInitScript(`localStorage.setItem('theme', ${JSON.stringify(theme)})`)
  }
  const page = await context.newPage()
  try {
    return await body(page)
  } finally {
    await context.close()
  }
}

/** Every element that paints or borders with one of the given colours. */
async function paintedWith(page, colors) {
  return page.evaluate(
    ({ colors, describe }) => {
      const describeOne = eval(describe)
      const wanted = new Set(colors)
      const hits = []
      for (const element of document.querySelectorAll('*')) {
        // Both the rail and the tab bar are always in the DOM — only one of
        // them is displayed. getComputedStyle answers for a display:none
        // element too, so without this the check reports the phone's accent
        // use at desktop widths and vice versa.
        if (!element.checkVisibility({ checkVisibilityCSS: true })) continue
        const style = getComputedStyle(element)
        const roles = []
        if (wanted.has(style.backgroundColor)) roles.push('background')
        if (wanted.has(style.color)) roles.push('color')
        for (const side of ['Top', 'Right', 'Bottom', 'Left']) {
          if (wanted.has(style[`border${side}Color`]) && style[`border${side}Width`] !== '0px') {
            roles.push(`border-${side.toLowerCase()}`)
          }
        }
        if (roles.length > 0) hits.push({ roles, ...describeOne(element) })
      }
      return hits
    },
    { colors, describe: DESCRIBE },
  )
}

/** The custom properties the stylesheet defines on :root, as the browser resolves them. */
async function palette(page) {
  return page.evaluate(() => {
    const names = new Set()
    for (const sheet of document.styleSheets) {
      let rules
      try {
        rules = sheet.cssRules
      } catch {
        continue
      }
      const walk = (list) => {
        for (const rule of list) {
          if (rule.cssRules) walk(rule.cssRules)
          if (!rule.style) continue
          for (const property of rule.style) if (property.startsWith('--')) names.add(property)
        }
      }
      walk(rules)
    }
    const style = getComputedStyle(document.documentElement)
    const out = {}
    for (const name of [...names].sort()) out[name] = style.getPropertyValue(name).trim()
    return out
  })
}

async function bookingPageReady(page, slug = 'consulting') {
  await page.goto(`${BASE}/book/${slug}`, { waitUntil: 'networkidle' })
  await page.waitForSelector('.slot, .empty', { timeout: 15000 })
}

async function adminReady(page, path) {
  await page.goto(`${BASE}/admin${path}`, { waitUntil: 'networkidle' })
  await page.waitForSelector('.admin-shell', { timeout: 15000 })
  // The shell's own settings read resolves after first paint; the identity block
  // is what it draws, so waiting for the network to go quiet is not enough.
  await page.waitForTimeout(500)
}

/**
 * The link editor is not routed on its own — Links.tsx swaps it in over the
 * list once an existing link's Edit button is clicked (see Links.tsx and
 * RecordTable.tsx: `td[data-column="Edit"] > button`, inside `.table tbody`).
 * Caller must already be on /admin/links. Returns whether the editor was
 * actually reached, and why not when it wasn't — a check that measured the
 * list instead and called it the editor would be a false pass.
 */
async function openFirstLink(page) {
  if ((await page.$('.empty')) !== null) {
    return { opened: false, reason: 'no seeded links — /admin/links rendered .empty, not a .table' }
  }
  const rowCount = await page.$$eval('.table tbody tr', (rows) => rows.length).catch(() => 0)
  if (rowCount === 0) {
    return { opened: false, reason: 'no rows in .table tbody on /admin/links' }
  }
  const editButton = await page.$('.table tbody tr:first-child td[data-column="Edit"] button')
  if (editButton === null) {
    return {
      opened: false,
      reason: 'first row has no button at td[data-column="Edit"] > button — table shape does not match Links.tsx',
    }
  }
  await editButton.click()
  try {
    await page.waitForSelector('.form-col', { timeout: 5000 })
  } catch {
    return { opened: false, reason: "clicked the first row's Edit button but .form-col never appeared" }
  }
  return { opened: true, reason: null }
}

/**
 * The pattern editor is not routed to by guessing `/availability/{id}` —
 * Availability.tsx lists patterns as `.grid-cards .card` articles, each with
 * an `a.btn` labelled "Edit" pointing at that pattern's own editor (see
 * Availability.tsx: the `<Link className="btn grow" href={...}>Edit</Link>`
 * inside `.grid-cards article.card`). Caller must already be on
 * /admin/availability. Returns whether the editor was actually reached, and
 * why not when it wasn't — measuring the list and calling it the editor
 * would be a false pass this check exists to refuse.
 */
async function openFirstPattern(page) {
  if ((await page.$('.empty')) !== null) {
    return { opened: false, reason: 'no seeded patterns — /admin/availability rendered .empty, not .grid-cards' }
  }
  const cardCount = await page.$$eval('.grid-cards .card', (nodes) => nodes.length).catch(() => 0)
  if (cardCount === 0) {
    return { opened: false, reason: 'no cards in .grid-cards on /admin/availability' }
  }
  const editLink = await page.$('.grid-cards .card:first-child a.btn')
  if (editLink === null) {
    return {
      opened: false,
      reason: 'first card has no a.btn — card shape does not match Availability.tsx',
    }
  }
  await editLink.click()
  try {
    await page.waitForSelector('.editor-cols', { timeout: 5000 })
  } catch {
    return { opened: false, reason: "clicked the first pattern's Edit link but .editor-cols never appeared" }
  }
  return { opened: true, reason: null }
}

const main = async () => {
  const browser = await chromium.launch()

  // ---- 1. Booking page at 440x956: stacked panes, one primary action, bottom edge.
  await withPage(browser, { viewport: PHONE }, async (page) => {
    await bookingPageReady(page)
    const panes = await page.$$eval('.booking-panes > section', (nodes) =>
      nodes.map((n) => {
        const r = n.getBoundingClientRect()
        return { x: r.x, y: r.y, width: r.width, height: r.height }
      }),
    )
    const beforeSelection = await page.$$eval('.btn-primary', (nodes) =>
      nodes.filter((n) => n.getBoundingClientRect().width > 0).length,
    )
    // env() cannot be read from script, so a probe property is declared that
    // resolves it. Headless Chromium reports every inset as 0 — there is no
    // notch to emulate — so this measures the rule, not a real device's inset.
    await page.addStyleTag({
      content: 'html { --probe-safe-bottom: env(safe-area-inset-bottom, 0px); }',
    })
    await page.click('.slot >> nth=0')
    await page.waitForSelector('.actionbar', { timeout: 5000 })
    const afterSelection = await page.$$eval('.btn-primary', (nodes) =>
      nodes
        .filter((n) => n.getBoundingClientRect().width > 0)
        .map((n) => {
          const r = n.getBoundingClientRect()
          return { text: n.textContent.trim().slice(0, 40), y: r.y, height: r.height }
        }),
    )
    const bar = await page.$eval('.actionbar', (n) => {
      const r = n.getBoundingClientRect()
      const style = getComputedStyle(n)
      return {
        top: r.top,
        bottom: r.bottom,
        paddingBottom: style.paddingBottom,
        position: style.position,
        viewportHeight: window.innerHeight,
        safeAreaBottom: getComputedStyle(document.documentElement)
          .getPropertyValue('--probe-safe-bottom')
          .trim(),
      }
    })
    await page.screenshot({ path: `${OUT}/phone-440-booking.png`, fullPage: false })
    record('phone-440', { panes, primaryActionsBeforeSelection: beforeSelection, primaryActionsAfterSelection: afterSelection, actionbar: bar })
  })

  // ---- 2. Duration 25 -> 50 changes both the times and the days.
  await withPage(browser, { viewport: DESKTOP }, async (page) => {
    await bookingPageReady(page)
    const readMonth = () =>
      page.evaluate(() => ({
        times: [...document.querySelectorAll('.slot')].map((n) => n.textContent.trim()),
        available: [...document.querySelectorAll('[data-state="available"]')].map((n) =>
          n.textContent.trim(),
        ),
        unavailable: [...document.querySelectorAll('[data-state="unavailable"]')].map((n) =>
          n.textContent.trim(),
        ),
      }))
    // Part A's fixture puts the override that closes a day in September, so the
    // month has to be advanced before the day set can move at all.
    await page.click('[aria-label="Next month"]')
    await page.waitForTimeout(800)
    await page.click('[data-state="available"] >> nth=0')
    await page.waitForTimeout(600)
    const at25 = await readMonth()
    const durations = await page.$$eval('.duration button, .duration-picker button, [role="radiogroup"] button', (nodes) =>
      nodes.map((n) => n.textContent.trim()),
    )
    await page.getByRole('button', { name: /50/ }).first().click()
    await page.waitForTimeout(900)
    const at50 = await readMonth()
    record('duration-25-to-50', {
      durationControls: durations,
      timesAt25: at25.times,
      timesAt50: at50.times,
      availableAt25: at25.available,
      availableAt50: at50.available,
      closedByTheSwitch: at25.available.filter((d) => !at50.available.includes(d)),
      timesChanged: JSON.stringify(at25.times) !== JSON.stringify(at50.times),
      daysChanged: JSON.stringify(at25.available) !== JSON.stringify(at50.available),
    })
  })

  // ---- 3. Tabular figures, and a column that does not shift width.
  const numericScan = `(describe) => {
      const describeOne = eval(describe)
      const isNumber = /\d/
      const out = []
      for (const element of document.querySelectorAll('*')) {
        if (element.children.length > 0) continue
        if (!element.checkVisibility({ checkVisibilityCSS: true })) continue
        const text = (element.textContent ?? '').trim()
        if (text === '' || !isNumber.test(text)) continue
        out.push(describeOne(element))
      }
      return out
    }`

  for (const [label, path] of [
    ['admin-links', '/links'],
    ['admin-meetings', '/meetings'],
    ['admin-availability', '/availability'],
    ['admin-settings', '/settings'],
  ]) {
    await withPage(browser, { viewport: DESKTOP }, async (page) => {
      await adminReady(page, path)
      const found = await page.evaluate(eval(numericScan), DESCRIBE)
      const inputs = await page.$$eval('input, select', (nodes) =>
        nodes
          .filter((n) => n.checkVisibility({ checkVisibilityCSS: true }))
          .map((n) => ({
            tag: n.tagName.toLowerCase(),
            type: n.getAttribute('type'),
            className: n.getAttribute('class'),
            value: (n.value ?? '').slice(0, 30),
            fontVariantNumeric: getComputedStyle(n).fontVariantNumeric,
          })),
      )
      record(`tabular-${label}`, {
        inputsLackingTabular: inputs.filter(
          (n) => !/tabular-nums/.test(n.fontVariantNumeric) && /\d/.test(n.value),
        ),
        total: found.length,
        lacking: found
          .filter((n) => !/tabular-nums/.test(n.fontVariantNumeric))
          .map((n) => ({ tag: n.tag, className: n.className, text: n.text })),
      })
    })
  }

  await withPage(browser, { viewport: DESKTOP }, async (page) => {
    await bookingPageReady(page)
    const numericElements = await page.evaluate((describe) => {
      const describeOne = eval(describe)
      const isNumber = /\d/
      const out = []
      for (const element of document.querySelectorAll('*')) {
        if (element.children.length > 0) continue
        const text = (element.textContent ?? '').trim()
        if (text === '' || !isNumber.test(text)) continue
        out.push(describeOne(element))
      }
      return out
    }, DESCRIBE)
    const widthsDayOne = await page.$$eval('.slot', (nodes) =>
      nodes.map((n) => n.getBoundingClientRect().width),
    )
    const daysOnScreen = await page.$$eval('[data-state="available"]', (nodes) =>
      nodes.map((n) => n.textContent.trim()),
    )
    await page.click('[data-state="available"] >> nth=1')
    await page.waitForTimeout(700)
    const widthsDayTwo = await page.$$eval('.slot', (nodes) =>
      nodes.map((n) => n.getBoundingClientRect().width),
    )
    record('tabular-figures', {
      lackingTabular: numericElements.filter((n) => !/tabular-nums/.test(n.fontVariantNumeric)),
      withTabular: numericElements.filter((n) => /tabular-nums/.test(n.fontVariantNumeric)).length,
      daysOnScreen: daysOnScreen.slice(0, 4),
      slotWidthsDayOne: [...new Set(widthsDayOne)],
      slotWidthsDayTwo: [...new Set(widthsDayTwo)],
    })
  })

  // ---- 4. Theme forced against the system preference, in both directions.
  const themeReadings = []
  for (const [stored, scheme] of [
    [null, 'light'],
    [null, 'dark'],
    ['dark', 'light'],
    ['light', 'dark'],
  ]) {
    await withPage(browser, { viewport: DESKTOP, colorScheme: scheme, theme: stored }, async (page) => {
      // Read in a rAF registered before any page script, so what is captured is
      // what was about to be painted rather than what settled afterwards.
      await page.addInitScript(`
        window.__firstFrame = new Promise((resolve) => {
          requestAnimationFrame(() => {
            const style = getComputedStyle(document.documentElement)
            resolve({
              attr: document.documentElement.getAttribute('data-theme'),
              page: style.getPropertyValue('--surface-page').trim(),
              accent: style.getPropertyValue('--accent').trim(),
              onAccent: style.getPropertyValue('--text-on-accent').trim(),
            })
          })
        })
      `)
      await bookingPageReady(page)
      const firstFrame = await page.evaluate(() => window.__firstFrame)
      themeReadings.push({ stored: stored ?? 'nothing', osScheme: scheme, ...firstFrame })
    })
  }
  record('theme-override', themeReadings)

  // ---- 5. Contrast of every text colour that sits on the accent, both themes.
  for (const [label, scheme, stored] of [
    ['light', 'light', null],
    ['dark', 'dark', null],
  ]) {
    await withPage(browser, { viewport: DESKTOP, colorScheme: scheme, theme: stored }, async (page) => {
      await bookingPageReady(page)
      await page.click('.slot >> nth=0')
      await page.waitForTimeout(300)
      const pairs = await page.evaluate((describe) => {
        const describeOne = eval(describe)
        const parse = (value) => value.match(/[\d.]+/g).slice(0, 3).map(Number)
        const channel = (c) => {
          const s = c / 255
          return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4
        }
        const luminance = ([r, g, b]) =>
          0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b)
        const ratio = (a, b) => {
          const [x, y] = [luminance(a), luminance(b)].sort((m, n) => n - m)
          return (x + 0.05) / (y + 0.05)
        }
        const accent = getComputedStyle(document.documentElement)
          .getPropertyValue('--accent')
          .trim()
        const probe = document.createElement('span')
        probe.style.color = accent
        document.body.append(probe)
        const accentRgb = parse(getComputedStyle(probe).color)
        probe.remove()
        const out = []
        for (const element of document.querySelectorAll('*')) {
          const style = getComputedStyle(element)
          const bg = parse(style.backgroundColor)
          if (bg.join() !== accentRgb.join()) continue
          const text = (element.textContent ?? '').trim()
          if (text === '') continue
          out.push({
            ...describeOne(element),
            contrast: Number(ratio(parse(style.color), bg).toFixed(2)),
          })
        }
        return { accent, accentRgb, on: out }
      }, DESCRIBE)
      record(`contrast-on-accent-${label}`, pairs)
    })
  }

  // ---- 6. Keyboard reaches every control, with a ring that is not clipped.
  for (const [label, path, viewport] of [
    ['booking', null, DESKTOP],
    ['admin-links', '/links', DESKTOP],
    ['admin-settings', '/settings', DESKTOP],
  ]) {
    await withPage(browser, { viewport }, async (page) => {
      if (path === null) await bookingPageReady(page)
      else await adminReady(page, path)

      const controls = await page.evaluate(() => {
        const selector =
          'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'
        return [...document.querySelectorAll(selector)]
          .filter((n) => n.getBoundingClientRect().width > 0)
          .map((n) => `${n.tagName.toLowerCase()}:${(n.textContent ?? '').trim().slice(0, 30) || n.getAttribute('aria-label') || n.getAttribute('name') || ''}`)
      })

      const reached = []
      const rings = []
      const seen = new Set()
      for (let i = 0; i < controls.length + 12; i += 1) {
        await page.keyboard.press('Tab')
        const focused = await page.evaluate((describe) => {
          const describeOne = eval(describe)
          const element = document.activeElement
          if (!element || element === document.body) return null
          const info = describeOne(element)
          // Does anything between this control and the page clip its ring?
          let node = element.parentElement
          const clippers = []
          while (node && node !== document.documentElement) {
            const style = getComputedStyle(node)
            if (/hidden|auto|scroll|clip/.test(style.overflowX + style.overflowY)) {
              const outer = node.getBoundingClientRect()
              const inner = element.getBoundingClientRect()
              clippers.push({
                clipper: node.getAttribute('class'),
                overflow: `${style.overflowX}/${style.overflowY}`,
                slackTop: inner.top - outer.top,
                slackBottom: outer.bottom - inner.bottom,
                slackLeft: inner.left - outer.left,
                slackRight: outer.right - inner.right,
              })
            }
            node = node.parentElement
          }
          return { ...info, clippers }
        }, DESCRIBE)
        if (focused === null) break
        const key = `${focused.tag}:${focused.text}:${focused.rect.x},${focused.rect.y}`
        if (seen.has(key)) break
        seen.add(key)
        reached.push(`${focused.tag}:${focused.text || focused.className}`)
        rings.push({
          control: `${focused.tag}:${focused.text || focused.className}`,
          outline: focused.outline,
          outlineOffset: focused.outlineOffset,
          boxShadow: focused.boxShadow,
          clippers: focused.clippers.filter(
            (c) =>
              Math.min(c.slackTop, c.slackBottom, c.slackLeft, c.slackRight) < 4 &&
              c.overflow !== 'visible/visible',
          ),
        })
      }
      record(`keyboard-${label}`, {
        controlsOnPage: controls.length,
        controlsReached: reached.length,
        reached,
        ringsWithoutVisibleOutline: rings.filter(
          (r) => (r.outline === 'none' || /rgba\(0, 0, 0, 0\)/.test(r.outline)) && r.boxShadow === 'none',
        ),
        ringsAtRiskOfClipping: rings.filter((r) => r.clippers.length > 0),
        sampleRing: rings[0],
      })
    })
  }

  // ---- 6b. What a focused form control actually shows.
  await withPage(browser, { viewport: DESKTOP }, async (page) => {
    await adminReady(page, '/settings')
    const readings = []
    for (const selector of ['input.input', 'select.select']) {
      await page.focus(`${selector} >> nth=0`)
      // The focus styles transition, so an immediate read catches the "from"
      // frame and reports no ring on a control that has one.
      await page.waitForTimeout(400)
      readings.push(
        await page.$eval(`${selector} >> nth=0`, (n) => {
          const style = getComputedStyle(n)
          return {
            selector: n.className,
            outline: style.outline,
            outlineWidth: style.outlineWidth,
            borderColor: style.borderTopColor,
            boxShadow: style.boxShadow,
          }
        }),
      )
    }
    record('focus-on-form-controls', readings)
  })

  // ---- 7. Inline style attributes in the built markup, on both surfaces.
  await withPage(browser, { viewport: DESKTOP }, async (page) => {
    await bookingPageReady(page)
    const booking = await page.$$eval('[style]', (nodes) =>
      nodes.map((n) => ({
        tag: n.tagName.toLowerCase(),
        className: n.getAttribute('class'),
        style: n.getAttribute('style'),
      })),
    )
    await adminReady(page, '/links')
    const links = await page.$$eval('[style]', (nodes) =>
      nodes.map((n) => ({ tag: n.tagName.toLowerCase(), className: n.getAttribute('class'), style: n.getAttribute('style') })),
    )
    await adminReady(page, '/meetings')
    const meetings = await page.$$eval('[style]', (nodes) =>
      nodes.map((n) => ({ tag: n.tagName.toLowerCase(), className: n.getAttribute('class'), style: n.getAttribute('style') })),
    )
    record('inline-styles', {
      bookingCount: booking.length,
      bookingSample: booking.slice(0, 3),
      bookingDistinct: [...new Set(booking.map((b) => `${b.tag}.${b.className}`))],
      adminLinks: links,
      adminMeetings: meetings,
    })
  })

  // ---- 8. Two Jobs: what the accent paints, on each surface.
  for (const [label, path] of [
    ['admin-links', '/links'],
    ['admin-meetings', '/meetings'],
    ['admin-settings', '/settings'],
  ]) {
    for (const viewport of [DESKTOP, TABBAR]) {
      await withPage(browser, { viewport }, async (page) => {
        await adminReady(page, path)
        const accents = await page.evaluate(() => {
          const style = getComputedStyle(document.documentElement)
          const probe = document.createElement('span')
          document.body.append(probe)
          const resolve = (name) => {
            probe.style.color = style.getPropertyValue(name).trim()
            return getComputedStyle(probe).color
          }
          const out = {
            accent: resolve('--accent'),
            accentHover: resolve('--accent-hover'),
            accentSubtle: resolve('--accent-subtle'),
            accentText: resolve('--accent-text'),
            focusRing: resolve('--focus-ring'),
          }
          probe.remove()
          return out
        })
        const colors = [accents.accent, accents.accentHover].filter((c) => c && c !== 'rgb(0, 0, 0)')
        const hits = await paintedWith(page, colors)
        record(`accent-${label}-${viewport.width}`, {
          accentTokens: accents,
          painted: hits.map((h) => ({ roles: h.roles, tag: h.tag, className: h.className, text: h.text })),
        })
      })
    }
  }

  // ---- 9. Second Channel: monochrome screenshots.
  for (const [label, path, viewport] of [
    ['links-1280', '/links', DESKTOP],
    ['links-899', '/links', TABBAR],
    ['links-759', '/links', RECORDS],
    ['meetings-1280', '/meetings', DESKTOP],
    ['meetings-899', '/meetings', TABBAR],
    ['shell-1280', '/settings', DESKTOP],
    ['shell-899', '/settings', TABBAR],
  ]) {
    await withPage(browser, { viewport }, async (page) => {
      await adminReady(page, path)
      await page.screenshot({ path: `${OUT}/color-${label}.png`, fullPage: false })
      await page.addStyleTag({ content: 'html { filter: grayscale(1) !important; }' })
      await page.waitForTimeout(200)
      await page.screenshot({ path: `${OUT}/mono-${label}.png`, fullPage: false })
    })
  }

  // ---- 10. The three admin breakpoints.
  await withPage(browser, { viewport: DESKTOP }, async (page) => {
    await adminReady(page, '/links')
    const shape = await page.evaluate((describe) => {
      const describeOne = eval(describe)
      const visible = (n) => n !== null && n.getBoundingClientRect().width > 0
      const rail = document.querySelector('.rail')
      const tabbar = document.querySelector('.tabbar')
      const current = document.querySelector('.rail .nav [aria-current="page"]')
      const sibling = document.querySelector('.rail .nav a:not([aria-current])')
      return {
        railVisible: visible(rail),
        railWidth: rail ? rail.getBoundingClientRect().width : null,
        tabbarVisible: visible(tabbar),
        railItems: [...document.querySelectorAll('.rail .nav a')].map((n) => n.textContent.trim()),
        railFootItems: [...document.querySelectorAll('.rail-foot a')].map((n) => n.textContent.trim()),
        current: current ? describeOne(current) : null,
        sibling: sibling ? describeOne(sibling) : null,
      }
    }, DESCRIBE)
    record('breakpoint-1280', shape)
  })

  await withPage(browser, { viewport: TABBAR }, async (page) => {
    await adminReady(page, '/links')
    const shape = await page.evaluate((describe) => {
      const describeOne = eval(describe)
      const visible = (n) => n !== null && n.getBoundingClientRect().width > 0
      const current = document.querySelector('.tabbar-inner [aria-current="page"]')
      const sibling = document.querySelector('.tabbar-inner a:not([aria-current])')
      const settings = document.querySelector('.tabbar > a')
      return {
        railVisible: visible(document.querySelector('.rail')),
        tabbarVisible: visible(document.querySelector('.tabbar')),
        tabbarInnerItems: [...document.querySelectorAll('.tabbar-inner a')].map((n) => n.textContent.trim()),
        tabbarInnerColumns: getComputedStyle(document.querySelector('.tabbar-inner')).gridTemplateColumns,
        settingsOutsideGrid: settings ? describeOne(settings) : null,
        current: current ? describeOne(current) : null,
        sibling: sibling ? describeOne(sibling) : null,
        currentBefore: current
          ? getComputedStyle(current, '::before').backgroundColor + ' h=' + getComputedStyle(current, '::before').height
          : null,
      }
    }, DESCRIBE)
    record('breakpoint-899', shape)
  })

  await withPage(browser, { viewport: RECORDS }, async (page) => {
    await adminReady(page, '/meetings')
    const shape = await page.evaluate(() => {
      const table = document.querySelector('.table')
      if (table === null) return { note: 'no .table on this page' }
      const cells = [...table.querySelectorAll('tbody tr:first-child td')].map((cell) => ({
        column: cell.getAttribute('data-column'),
        text: cell.textContent.trim().slice(0, 40),
        beforeContent: getComputedStyle(cell, '::before').content,
        display: getComputedStyle(cell).display,
      }))
      return {
        headVisible: getComputedStyle(table.querySelector('thead')).display,
        rowDisplay: getComputedStyle(table.querySelector('tbody tr')).display,
        cells,
      }
    })
    record('breakpoint-759', shape)
  })

  // ---- 10b. The manage page, which is the third surface a stranger reaches.
  const manageToken = process.env.KAIROS_MANAGE_TOKEN ?? ''
  if (manageToken !== '') {
    await withPage(browser, { viewport: DESKTOP }, async (page) => {
      await page.goto(`${BASE}/manage/${manageToken}`, { waitUntil: 'networkidle' })
      await page.waitForSelector('.summary-when, .prose')
      const scan = await page.evaluate((describe) => {
        const describeOne = eval(describe)
        const out = []
        for (const element of document.querySelectorAll('*')) {
          if (element.children.length > 0) continue
          if (!element.checkVisibility({ checkVisibilityCSS: true })) continue
          const text = (element.textContent ?? '').trim()
          if (text === '' || !/\d/.test(text)) continue
          out.push(describeOne(element))
        }
        return out
      }, DESCRIBE)
      const inline = await page.$$eval('[style]', (nodes) =>
        nodes.map((n) => ({ tag: n.tagName.toLowerCase(), className: n.getAttribute('class'), style: n.getAttribute('style') })),
      )
      record('manage-page', {
        numericTotal: scan.length,
        numericLacking: scan
          .filter((n) => !/tabular-nums/.test(n.fontVariantNumeric))
          .map((n) => ({ tag: n.tag, className: n.className, text: n.text })),
        inlineStyles: inline.length,
      })
    })
  }

  // ---- 11. Inversion: the two palettes side by side.
  const palettes = {}
  for (const scheme of ['light', 'dark']) {
    await withPage(browser, { viewport: DESKTOP, colorScheme: scheme }, async (page) => {
      await bookingPageReady(page)
      palettes[scheme] = await palette(page)
    })
  }
  const inversion = Object.keys(palettes.light).map((name) => ({
    token: name,
    light: palettes.light[name],
    dark: palettes.dark[name],
  }))
  record('inversion', inversion)

  // ---- 12. Narrow column: control widths, the label track beside them, the
  // tap floor, and page overflow, on the two screens this branch moved to a
  // 620px column, at the widths a real screenshot and a real operator
  // actually see.
  //
  // The four size classes' intended pixel widths, read out of kairos.css
  // (.ctl-num/.ctl-sm/.ctl-md/.ctl-lg: 5.5rem/11rem/18rem/28rem, and no `html`
  // font-size rule anywhere in the sheet, so 1rem is the browser default of
  // 16px). A .textarea carries none of the four classes and is exempt by
  // construction: it never matches `sizeClass` below. These caps are a
  // desktop-only contract — see readFieldRowBreakpoint below — and are the
  // wrong check on a phone, where kairos.css deliberately overrides them.
  const CTL_WIDTH = { 'ctl-num': 88, 'ctl-sm': 176, 'ctl-md': 288, 'ctl-lg': 448 }
  // getBoundingClientRect widths are subpixel; on a control CSS sets to
  // width:100% of its track, a fraction-of-a-pixel gap between the two is
  // rounding, not a control that failed to go full width.
  const FULL_WIDTH_SLACK = 1

  /**
   * The px value of the max-width query that collapses .field-row to one
   * column and sends its own .input/.select/.prefix children to width:100%
   * (kairos.css, `.field-row-control > .input, > .select, > .prefix`).
   * Read out of the page's own served stylesheet rather than hardcoded: a
   * separate effort is editing kairos.css alongside this branch, and a
   * literal 640 would go stale the moment that number moved without this
   * check noticing.
   *
   * kairos.css nests every rule inside `@layer components`, so the target is
   * three levels down — CSSLayerBlockRule > CSSMediaRule > CSSStyleRule — not
   * one. Every grouping rule exposes `.cssRules`, but so, under the CSS
   * Nesting spec, does a plain CSSStyleRule with nothing nested inside it —
   * as an always-present, usually *empty* CSSRuleList. Recursing on mere
   * truthiness (verified against a live CSSStyleRule with Playwright's own
   * Chromium: `!!rule.cssRules` is true, `rule.cssRules.length` is 0)
   * silently swallowed every leaf into a no-op recursion and its own
   * selectorText was never checked — the actual reason this returned null
   * against the live instance, not the regex. Recursing only when there is
   * something to descend into (`rule.cssRules.length > 0`), and checking a
   * rule's own selectorText independently of whether it also has children,
   * fixes both: a real grouping rule with content still recurses, and an
   * ordinary leaf falls straight through to the selector check.
   *
   * The selector match itself is whitespace-normalised `includes`, not a
   * split-and-strict-equality: `rule.selectorText` serialises the whole
   * comma-joined group as one string, and two rules on this exact path share
   * a `.field-row-control` prefix, so the needle has to be normalised the
   * same way as the haystack and specific enough (`> .input`) to land on the
   * rule that actually sets `width: 100%`, not the bare one above it.
   */
  const readFieldRowBreakpoint = async (page) =>
    page.evaluate(() => {
      const normalize = (text) => text.replace(/\s+/g, ' ').trim()
      const TARGET = normalize('.field-row-control > .input')
      let breakpoint = null
      let mediaText = null
      const walk = (rules, maxWidth, currentMediaText) => {
        for (const rule of rules) {
          let innerMaxWidth = maxWidth
          let innerMediaText = currentMediaText
          if (rule.media) {
            const match = rule.media.mediaText.match(/max-width:\s*([\d.]+)px/)
            if (match) {
              innerMaxWidth = Number(match[1])
              innerMediaText = rule.media.mediaText
            }
          }
          if (
            innerMaxWidth !== null &&
            rule.selectorText &&
            normalize(rule.selectorText).includes(TARGET)
          ) {
            breakpoint = innerMaxWidth
            mediaText = innerMediaText
          }
          if (rule.cssRules && rule.cssRules.length > 0) {
            walk(rule.cssRules, innerMaxWidth, innerMediaText)
          }
        }
      }
      for (const sheet of document.styleSheets) {
        let rules
        try {
          rules = sheet.cssRules
        } catch {
          continue
        }
        walk(rules, null, null)
        if (breakpoint !== null) break
      }
      return { breakpoint, mediaText }
    })

  /** A CSS custom property on :root, resolved to a number of px, or null. */
  const readCssPixelVar = async (page, name) => {
    const raw = await page.evaluate(
      (propName) => getComputedStyle(document.documentElement).getPropertyValue(propName).trim(),
      name,
    )
    const value = Number.parseFloat(raw)
    return Number.isNaN(value) ? null : value
  }

  const measureNarrowControls = async (page) =>
    page.evaluate(() => {
      const out = []
      for (const element of document.querySelectorAll('.input, .select, .textarea')) {
        if (!element.checkVisibility({ checkVisibilityCSS: true })) continue
        const rect = element.getBoundingClientRect()
        const track = element.closest('.field-row-control')
        out.push({
          tag: element.tagName.toLowerCase(),
          id: element.id || null,
          classes: (element.getAttribute('class') ?? '').split(/\s+/).filter(Boolean),
          width: rect.width,
          height: rect.height,
          // .affix wraps a unit-affixed number one level inside
          // .field-row-control (InheritableNumber.tsx, DurationEditor.tsx,
          // Settings.tsx). kairos.css's phone full-width rule targets only
          // .field-row-control's own direct children, so a control nested in
          // .affix is excluded from it by the child combinator and
          // legitimately stays compact on a phone.
          inAffix: element.closest('.affix') !== null,
          controlTrackWidth: track ? track.getBoundingClientRect().width : null,
        })
      }
      return out
    })

  /**
   * Every .field-row: its grid's column count, and its label track's width,
   * line count, and the two signals that actually distinguish "wraps onto a
   * sane number of lines because it is two sentences of help text" from
   * "wraps one word per line because the track is too narrow to be prose":
   *
   * - `overflowing`: scrollWidth > clientWidth on the label element. In a
   *   correct layout the label's content always fits its own track, so this
   *   is 0/false everywhere nothing is wrong and true the moment it isn't.
   * - `widestWordExceedsTrack`: the widest single word across every
   *   text-bearing element inside the label (a <label> and a .fine help
   *   paragraph can carry different fonts, so each is render-measured in its
   *   own via a scratch canvas), compared against the label's own track
   *   width. A track narrower than its own longest word cannot lay out as
   *   prose at any line count — that is the one-word-per-line defect this
   *   check exists to catch.
   *
   * Line count is reported as data, not as a threshold: a genuinely long
   * help sentence wrapping to three lines in a narrow column is correct, and
   * flagging it on line count alone was a false positive removed from here.
   */
  const measureFieldRows = async (page) =>
    page.evaluate(() => {
      const canvas = document.createElement('canvas')
      const ctx = canvas.getContext('2d')
      const longestWordWidth = (element) => {
        const style = getComputedStyle(element)
        ctx.font = `${style.fontWeight} ${style.fontSize} ${style.fontFamily}`
        const words = (element.textContent ?? '').trim().split(/\s+/).filter(Boolean)
        let max = 0
        for (const word of words) {
          const w = ctx.measureText(word).width
          if (w > max) max = w
        }
        return max
      }

      const out = []
      for (const row of document.querySelectorAll('.field-row')) {
        if (!row.checkVisibility({ checkVisibilityCSS: true })) continue
        const columns = getComputedStyle(row).gridTemplateColumns.trim().split(/\s+/).filter(Boolean)
        const label = row.querySelector('.field-row-label')
        let labelInfo = null
        if (label !== null && label.checkVisibility({ checkVisibilityCSS: true })) {
          const rect = label.getBoundingClientRect()
          const lineHeight = Number.parseFloat(getComputedStyle(label).lineHeight)
          const textLeaves = [...label.querySelectorAll('*')].filter((el) => el.children.length === 0)
          const leaves = textLeaves.length > 0 ? textLeaves : [label]
          const widestWord = Math.max(0, ...leaves.map(longestWordWidth))
          labelInfo = {
            text: (label.textContent ?? '').trim().slice(0, 60),
            width: rect.width,
            scrollWidth: label.scrollWidth,
            clientWidth: label.clientWidth,
            lines:
              Number.isFinite(lineHeight) && lineHeight > 0
                ? Math.round(label.clientHeight / lineHeight)
                : null,
            overflowing: label.scrollWidth > label.clientWidth,
            widestWord: Number(widestWord.toFixed(1)),
            widestWordExceedsTrack: widestWord > label.clientWidth,
          }
        }
        out.push({ columnCount: columns.length, gridTemplateColumns: columns.join(' '), label: labelInfo })
      }
      return out
    })

  const narrowTargets = [
    [
      'links-editor',
      async (page) => {
        await adminReady(page, '/links')
        return openFirstLink(page)
      },
    ],
    [
      'settings',
      async (page) => {
        await adminReady(page, '/settings')
        return { opened: true, reason: null }
      },
    ],
  ]

  const narrowViewports = [
    ['2560x1400', WIDE],
    ['1440x900', LAPTOP],
    ['440x956', PHONE],
  ]

  for (const [routeLabel, prepare] of narrowTargets) {
    for (const [viewportLabel, viewport] of narrowViewports) {
      await withPage(browser, { viewport }, async (page) => {
        const name = `narrow-${routeLabel}-${viewportLabel}`
        const ready = await prepare(page)
        if (!ready.opened) {
          record(name, { reachable: false, reason: ready.reason })
          return
        }

        const { breakpoint, mediaText } = await readFieldRowBreakpoint(page)
        // The desktop size-class caps and the phone full-width contract are
        // mutually exclusive: below the breakpoint the caps are not the rule
        // kairos.css enforces, and checking them there is exactly what
        // produced the false positive on the pattern <select> at 440. If the
        // stylesheet's own rule can't be found — or parses to something that
        // isn't a real breakpoint — a silent fallback to the desktop contract
        // reproduces that false positive and reads as a real finding, so this
        // fails loudly instead of guessing. mediaText travels alongside the
        // parsed number in both the error and the record, so a future reader
        // can see the raw string and the number side by side and judge the
        // parse themselves rather than trust it blind.
        if (breakpoint === null || !Number.isFinite(breakpoint) || breakpoint <= 0) {
          throw new Error(
            `readFieldRowBreakpoint could not parse a positive max-width around ".field-row-control > .input" in the served stylesheet for ${name} — got breakpoint=${breakpoint} from mediaText=${JSON.stringify(mediaText)} — refusing to fall back to the desktop size-class contract`,
          )
        }
        const tap = await readCssPixelVar(page, '--tap')
        const isPhoneLayout = viewport.width <= breakpoint

        const controls = await measureNarrowControls(page)
        const fieldRows = await measureFieldRows(page)
        const scrollWidth = await page.evaluate(() => document.documentElement.scrollWidth)

        let exceedingSizeClass = []
        let notFullWidth = []
        if (isPhoneLayout) {
          notFullWidth = controls
            .filter((control) => !control.inAffix && control.controlTrackWidth !== null)
            .filter((control) => control.width < control.controlTrackWidth - FULL_WIDTH_SLACK)
            .map((control) => ({
              ...control,
              shortOfTrackBy: Number((control.controlTrackWidth - control.width).toFixed(1)),
            }))
        } else {
          exceedingSizeClass = controls
            .map((control) => {
              const sizeClass = control.classes.find((className) => className in CTL_WIDTH)
              if (sizeClass === undefined) return null
              const maxWidth = CTL_WIDTH[sizeClass]
              if (control.width <= maxWidth) return null
              return { ...control, sizeClass, maxWidth, overBy: Number((control.width - maxWidth).toFixed(1)) }
            })
            .filter((control) => control !== null)
        }

        const underTapHeight =
          tap === null
            ? []
            : controls
                .filter((control) => control.height < tap)
                .map((control) => ({ ...control, shortBy: Number((tap - control.height).toFixed(1)) }))

        // "The row has collapsed to a single column" is only a meaningful
        // assertion under the stacked contract — a 2-column row above the
        // breakpoint is correct, not a finding.
        const rowsNotCollapsed = isPhoneLayout ? fieldRows.filter((row) => row.columnCount !== 1) : []
        const labels = fieldRows.map((row) => row.label).filter((label) => label !== null)
        const wrappingLabels = labels.filter((label) => label.overflowing || label.widestWordExceedsTrack)

        const fileBase = `${OUT}/${name}`
        await page.screenshot({ path: `${fileBase}.png`, fullPage: true })
        // The design system forbids state carried by colour alone: on the link
        // editor an overridden limit is a number field and an inherited one is
        // read-only prose with a Global badge, and that has to survive going
        // monochrome. Desaturating and shooting again is the evidence for a
        // human to judge that; this script does not decide it.
        await page.evaluate(() => {
          document.documentElement.style.filter = 'grayscale(1)'
        })
        await page.screenshot({ path: `${fileBase}-mono.png`, fullPage: true })

        record(name, {
          reachable: true,
          // Plain numbers beside scrollWidth, not a nested object — so a
          // reader of results.json can compare the two without knowing the
          // record's own name encodes the viewport.
          viewportWidth: viewport.width,
          viewportHeight: viewport.height,
          fieldRowBreakpoint: breakpoint,
          fieldRowBreakpointMediaText: mediaText,
          layoutContract: isPhoneLayout ? 'stacked' : 'desktop',
          tapFloor: tap,
          controlCount: controls.length,
          controls,
          exceedingSizeClass,
          notFullWidth,
          underTapHeight,
          fieldRowCount: fieldRows.length,
          labels,
          wrappingLabels,
          rowsNotCollapsed,
          scrollWidth,
          scrollsSideways: scrollWidth > viewport.width,
        })
      })
    }
  }

  // ---- 13. The pattern editor's date panel: the segmented date-state
  // control, everything the Own hours state adds beside it, and whether any
  // of it outgrows the ~340px right column DESIGN.md caps the preview pane
  // at — at the same three widths the narrow-column check above uses, since
  // a control that fits the operator's laptop can still overflow the
  // 2560px screenshot width or the 440px phone this branch also has to work
  // on.
  const readEditorColsBreakpoint = async (page) =>
    page.evaluate(() => {
      const TARGET = '.editor-cols'
      let breakpoint = null
      let mediaText = null
      // Same walk as readFieldRowBreakpoint above, and the same reason: a
      // grouping rule and a childless leaf both expose `.cssRules` under the
      // CSS Nesting spec, so recursion is gated on `.length > 0` rather than
      // on truthiness, and a rule's own selectorText is checked independently
      // of whether it also has children.
      const walk = (rules, minWidth, currentMediaText) => {
        for (const rule of rules) {
          let innerMinWidth = minWidth
          let innerMediaText = currentMediaText
          if (rule.media) {
            const match = rule.media.mediaText.match(/min-width:\s*([\d.]+)px/)
            if (match) {
              innerMinWidth = Number(match[1])
              innerMediaText = rule.media.mediaText
            }
          }
          if (innerMinWidth !== null && rule.selectorText && rule.selectorText.trim() === TARGET) {
            breakpoint = innerMinWidth
            mediaText = innerMediaText
          }
          if (rule.cssRules && rule.cssRules.length > 0) {
            walk(rule.cssRules, innerMinWidth, innerMediaText)
          }
        }
      }
      for (const sheet of document.styleSheets) {
        let rules
        try {
          rules = sheet.cssRules
        } catch {
          continue
        }
        walk(rules, null, null)
        if (breakpoint !== null) break
      }
      return { breakpoint, mediaText }
    })

  /**
   * The segmented date-state control: its own box, whether its content
   * overflows that box, and each of its three radio options against the tap
   * floor. Scoped from `[role="radiogroup"].segmented` rather than a
   * page-wide selector — `.segmented` also names the booking page's duration
   * picker, which this check never visits, but the radiogroup role is the
   * date-state control alone (see PatternPreview.tsx: the duration picker
   * uses `aria-pressed`, this control uses `aria-checked`).
   */
  const measureSegmented = async (page, tap) =>
    page.evaluate((tapFloor) => {
      const control = document.querySelector('.segmented[role="radiogroup"]')
      if (control === null) return { controlFound: false }
      const rect = control.getBoundingClientRect()
      const options = [...control.querySelectorAll('[role="radio"]')].map((option) => {
        const optionRect = option.getBoundingClientRect()
        return {
          text: (option.textContent ?? '').trim(),
          checked: option.getAttribute('aria-checked') === 'true',
          width: optionRect.width,
          height: optionRect.height,
        }
      })
      return {
        controlFound: true,
        width: rect.width,
        scrollWidth: control.scrollWidth,
        overflowsOwnBox: control.scrollWidth > rect.width,
        options,
        optionsUnderTapFloor:
          tapFloor === null
            ? []
            : options
                .filter((option) => option.height < tapFloor)
                .map((option) => ({ ...option, shortBy: Number((tapFloor - option.height).toFixed(1)) })),
      }
    }, tap)

  /**
   * Every control the date panel offers once Own hours is showing — each
   * window's two time fields and its × remove target, +Add window, and Save
   * this date — against the tap floor. Scoped to the radiogroup's own
   * `.sep-top` ancestor (the panel `selected !== null` renders — see
   * PatternPreview.tsx) rather than the whole page, since `.chip-x` and
   * `.btn-primary` are not unique to this panel.
   */
  const measurePanelControls = async (page, tap) =>
    page.evaluate((tapFloor) => {
      const group = document.querySelector('.segmented[role="radiogroup"]')
      const panel = group ? group.closest('.sep-top') : null
      if (panel === null) return { panelFound: false, controls: [] }
      const nodes = [
        ...panel.querySelectorAll('.week-time, .chip-x, [aria-label^="Add window to"], .btn-primary'),
      ].filter((node) => node.checkVisibility({ checkVisibilityCSS: true }))
      const controls = nodes.map((node) => {
        const rect = node.getBoundingClientRect()
        return {
          tag: node.tagName.toLowerCase(),
          className: node.getAttribute('class'),
          label: node.getAttribute('aria-label') || (node.textContent ?? '').trim().slice(0, 40),
          width: rect.width,
          height: rect.height,
        }
      })
      return {
        panelFound: true,
        controlCount: controls.length,
        controls,
        underTapFloor:
          tapFloor === null
            ? []
            : controls
                .filter((control) => control.height < tapFloor)
                .map((control) => ({ ...control, shortBy: Number((tapFloor - control.height).toFixed(1)) })),
      }
    }, tap)

  /** The date panel's own scrollWidth against the clientWidth of the card
   * that is its column — `.editor-cols`'s second grid item on the desktop
   * contract, the only item once the layout has stacked. Found via
   * `.closest('.card')` from the panel rather than a positional selector, so
   * this does not care which of WeekEditor's and PatternPreview's two
   * `.card.card-pad` sections the grid happens to put second. */
  const measureColumnFit = async (page) =>
    page.evaluate(() => {
      const group = document.querySelector('.segmented[role="radiogroup"]')
      const panel = group ? group.closest('.sep-top') : null
      const column = panel ? panel.closest('.card') : null
      if (panel === null || column === null) return { panelFound: false }
      return {
        panelFound: true,
        panelScrollWidth: panel.scrollWidth,
        columnClientWidth: column.clientWidth,
        panelExceedsColumn: panel.scrollWidth > column.clientWidth,
      }
    })

  /**
   * Whether a window row (WindowRows.tsx: a `.row` holding two `.week-time`
   * fields, a `–` separator and a `.chip-x`) has wrapped in the date panel.
   * jsdom never lays this out, so nothing in the unit suite catches it — this
   * check is what would have caught the regression it is named for: the
   * narrow date-panel column is ~276px, a window row's fixed-width fields and
   * gaps needed ~317px, and the row wrapped, dropping × onto its own line.
   *
   * Detected by height, not by the `top` of a row's children: a row that has
   * not wrapped at all still spreads its children's `top` across several
   * values, because `.row`'s own `align-items: center` centers a shorter
   * `–` separator against its taller `.week-time` siblings — that spread was
   * mistaken for wrapping once already and produced a false positive. Height
   * does not have that failure mode: a single line's row resolves to exactly
   * its tallest child's height plus its own vertical padding, and only an
   * actual second line pushes it taller than that. The padding is read off
   * the row's own computed style rather than assumed to be zero — `.row` in
   * kairos.css sets none today, but a future rule adding some should not
   * silently start reading as a wrap.
   */
  const measureWindowRows = async (page) =>
    page.evaluate(() => {
      const group = document.querySelector('.segmented[role="radiogroup"]')
      const panel = group ? group.closest('.sep-top') : null
      if (panel === null) return { panelFound: false, rows: [] }
      const rows = [...panel.querySelectorAll('.row')].filter(
        (row) => row.querySelector('.week-time') !== null,
      )
      return {
        panelFound: true,
        rows: rows.map((row) => {
          const rect = row.getBoundingClientRect()
          const style = getComputedStyle(row)
          const paddingBlock = Number.parseFloat(style.paddingTop) + Number.parseFloat(style.paddingBottom)
          const tallestChild = Math.max(
            0,
            ...[...row.children].map((child) => child.getBoundingClientRect().height),
          )
          return {
            width: rect.width,
            height: rect.height,
            tallestChild,
            paddingBlock,
            wrapped: rect.height > tallestChild + paddingBlock,
          }
        }),
      }
    })

  for (const [viewportLabel, viewport] of narrowViewports) {
    await withPage(browser, { viewport }, async (page) => {
      const name = `pattern-editor-${viewportLabel}`

      await adminReady(page, '/availability')
      const opened = await openFirstPattern(page)
      if (!opened.opened) {
        record(name, { reachable: false, reason: opened.reason })
        return
      }

      const daySelector = '.days button.day:not([disabled])'
      const dayCount = await page.$$eval(daySelector, (nodes) => nodes.length).catch(() => 0)
      if (dayCount === 0) {
        record(name, {
          reachable: false,
          reason: 'reached the pattern editor but no selectable day was found in the month grid',
        })
        return
      }
      await page.click(`${daySelector} >> nth=0`)
      try {
        await page.waitForSelector('.segmented[role="radiogroup"]', { timeout: 5000 })
      } catch {
        record(name, {
          reachable: false,
          reason: 'selected a date but the segmented date-state control never appeared',
        })
        return
      }

      // Own hours first: it is the state carrying the most controls, and the
      // one the screenshot and every measurement below is taken in.
      await page.getByRole('radio', { name: 'Own hours' }).click()
      await page.waitForTimeout(300)

      const tap = await readCssPixelVar(page, '--tap')
      const { breakpoint: editorColsBreakpoint, mediaText: editorColsMediaText } =
        await readEditorColsBreakpoint(page)
      // Same refusal as readFieldRowBreakpoint's caller above: a silent
      // fallback to a guessed number would read as a real finding rather
      // than as what it is, a parse that failed.
      if (editorColsBreakpoint === null || !Number.isFinite(editorColsBreakpoint) || editorColsBreakpoint <= 0) {
        throw new Error(
          `readEditorColsBreakpoint could not parse a positive min-width around ".editor-cols" in the served stylesheet for ${name} — got breakpoint=${editorColsBreakpoint} from mediaText=${JSON.stringify(editorColsMediaText)} — refusing to guess`,
        )
      }
      const layoutContract = viewport.width >= editorColsBreakpoint ? 'side-by-side' : 'stacked'

      const segmented = await measureSegmented(page, tap)
      const panelControls = await measurePanelControls(page, tap)
      const columnFit = await measureColumnFit(page)
      const windowRows = await measureWindowRows(page)
      const wrappedWindowRows = windowRows.rows.filter((row) => row.wrapped)
      const pageScrollWidth = await page.evaluate(() => document.documentElement.scrollWidth)

      await page.screenshot({ path: `${OUT}/${name}-own-hours.png`, fullPage: true })

      // Blocked second, numbers only: a longer label in a selected state,
      // drawn in the danger colour, is where an overflow would first show.
      await page.getByRole('radio', { name: 'Blocked' }).click()
      await page.waitForTimeout(300)
      const segmentedBlocked = await measureSegmented(page, tap)

      record(name, {
        reachable: true,
        viewportWidth: viewport.width,
        viewportHeight: viewport.height,
        editorColsBreakpoint,
        editorColsMediaText,
        layoutContract,
        tapFloor: tap,
        ownHours: {
          segmented,
          panelControls,
          columnFit,
          windowRows,
          wrappedWindowRows,
          pageScrollWidth,
          scrollsSideways: pageScrollWidth > viewport.width,
        },
        blocked: { segmented: segmentedBlocked },
      })
    })
  }

  // ---- 14. The propose panel: whether the operator's time picker fits the
  // width it is actually given. It was first written into the Actions cell of
  // RecordTable, and this check is what caught it — `.calendar-cols` resolved
  // its month track to 55px at 1280 and 18px at 899, because minmax(0, 300px)
  // has a zero minimum and shrinks silently rather than refusing, which put
  // seven day columns and their weekday headings on top of each other at 8px
  // and then 3px a square. The panel now swaps in over the list the way
  // LinkEditor does, and what this asserts is that every square stays at the
  // 44px tap floor's own 40px box and nothing scrolls sideways.
  for (const [label, viewport] of [
    ['propose-2560', WIDE],
    ['propose-1440', LAPTOP],
    ['propose-1280', DESKTOP],
    ['propose-899', TABBAR],
    ['propose-759', RECORDS],
    ['propose-440', PHONE],
  ]) {
    await withPage(browser, { viewport }, async (page) => {
      await adminReady(page, '/meetings')
      const opener = await page.$('button:has-text("Suggest a new time")')
      if (opener === null) {
        record(label, { opened: false, reason: 'no confirmed meeting on /admin/meetings to propose against' })
        return
      }
      await opener.click()
      await page.waitForSelector('.calendar-cols', { timeout: 10000 })
      // The slot column resolves after the month's slots read.
      await page.waitForTimeout(1500)
      await page.screenshot({ path: `${OUT}/${label}.png`, fullPage: true })

      // Picked, because the commit button only exists once a time is chosen and
      // its visibility is the second half of this check. Manage.tsx's
      // `.desktop-cta` is display:none below 861px — it pairs with a fixed
      // action bar the admin shell does not have — so a panel that borrowed it
      // would leave the operator on a phone with a selection and no way to send
      // it, at exactly the widths no unit test can see.
      const slot = await page.$('.slot')
      if (slot !== null) {
        await slot.click()
        await page.waitForTimeout(300)
      }

      record(label, await page.evaluate((describe) => {
        const describeOne = eval(describe)
        const box = (el) => {
          if (el === null) return null
          const r = el.getBoundingClientRect()
          return { x: Math.round(r.x), y: Math.round(r.y), width: Math.round(r.width), height: Math.round(r.height) }
        }
        const cols = document.querySelector('.calendar-cols')
        const days = document.querySelector('.days')
        const slots = document.querySelector('.slots')
        const doc = document.scrollingElement
        return {
          opened: true,
          viewportWidth: window.innerWidth,
          gridTemplateColumns: cols ? getComputedStyle(cols).gridTemplateColumns : null,
          calendarCols: box(cols),
          days: box(days),
          // The square is the whole question: seven columns in a track too
          // narrow for them is a calendar nobody can read, and no test with
          // no layout engine can see it.
          // A button rather than any .day: the leading blanks are spans, and
          // the question is the size of a square somebody has to press.
          firstDay: document.querySelector('button.day')
            ? describeOne(document.querySelector('button.day'))
            : null,
          slots: box(slots),
          firstSlot: document.querySelector('.slot') ? describeOne(document.querySelector('.slot')) : null,
          documentOverflowsHorizontally: doc.scrollWidth > doc.clientWidth,
          // A table left on screen under the picker would mean the swap did
          // not happen and the panel is back inside a cell.
          tableStillOnScreen: document.querySelector('.table') !== null,
          commit: (() => {
            const button = [...document.querySelectorAll('button')].find(
              (n) => /suggest this time/i.test(n.textContent ?? ''),
            )
            if (button === undefined) return { present: false }
            const r = button.getBoundingClientRect()
            return {
              present: true,
              visible: r.width > 0 && r.height > 0,
              width: Math.round(r.width),
              height: Math.round(r.height),
              display: getComputedStyle(button.parentElement).display,
            }
          })(),
        }
      }, DESCRIBE))
    })
  }

  await browser.close()
  writeFileSync(`${OUT}/results.json`, JSON.stringify(results, null, 2))
  console.log(`\nwrote ${OUT}/results.json and screenshots to ${OUT}/`)
}

main().catch((error) => {
  console.error(error)
  process.exit(1)
})
