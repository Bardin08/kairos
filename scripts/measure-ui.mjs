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
  // A help line wrapping past this many lines in a two-column .field-row is
  // the failure this check exists to catch. Not derivable from the
  // stylesheet — there is no CSS rule that names "too many lines" — so it is
  // a judgment call made a named constant rather than an inline 2.
  const LABEL_MAX_LINES = 2
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
   */
  const readFieldRowBreakpoint = async (page) =>
    page.evaluate(() => {
      const TARGET = '.field-row-control > .input'
      let found = null
      const walk = (rules, maxWidth) => {
        for (const rule of rules) {
          if (rule.media) {
            const match = rule.media.mediaText.match(/max-width:\s*([\d.]+)px/)
            walk(rule.cssRules, match ? Number(match[1]) : maxWidth)
          } else if (
            maxWidth !== null &&
            rule.selectorText &&
            rule.selectorText.split(',').some((selector) => selector.trim() === TARGET)
          ) {
            found = maxWidth
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
        walk(rules, null)
        if (found !== null) break
      }
      return found
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

  /** Every .field-row: its grid's column count, and its label track's width, overflow and line count. */
  const measureFieldRows = async (page) =>
    page.evaluate(() => {
      const out = []
      for (const row of document.querySelectorAll('.field-row')) {
        if (!row.checkVisibility({ checkVisibilityCSS: true })) continue
        const columns = getComputedStyle(row).gridTemplateColumns.trim().split(/\s+/).filter(Boolean)
        const label = row.querySelector('.field-row-label')
        let labelInfo = null
        if (label !== null && label.checkVisibility({ checkVisibilityCSS: true })) {
          const rect = label.getBoundingClientRect()
          const lineHeight = Number.parseFloat(getComputedStyle(label).lineHeight)
          labelInfo = {
            text: (label.textContent ?? '').trim().slice(0, 60),
            width: rect.width,
            scrollWidth: label.scrollWidth,
            lines:
              Number.isFinite(lineHeight) && lineHeight > 0
                ? Math.round(label.clientHeight / lineHeight)
                : null,
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

        const breakpoint = await readFieldRowBreakpoint(page)
        const tap = await readCssPixelVar(page, '--tap')
        // The desktop size-class caps and the phone full-width contract are
        // mutually exclusive: below the breakpoint the caps are not the rule
        // kairos.css enforces, so checking them there is what produced the
        // false positive on the pattern <select> at 440. `breakpoint === null`
        // means the stylesheet's own rule for this could not be found — treat
        // that as "desktop contract" rather than guessing, and say so in the
        // record instead of silently picking a behaviour.
        const isPhoneLayout = breakpoint !== null && viewport.width <= breakpoint

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
        const wrappingLabels = labels.filter((label) => label.lines !== null && label.lines > LABEL_MAX_LINES)

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
          layoutContract:
            breakpoint === null ? 'unknown — breakpoint rule not found in stylesheet' : isPhoneLayout ? 'stacked' : 'desktop',
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

  await browser.close()
  writeFileSync(`${OUT}/results.json`, JSON.stringify(results, null, 2))
  console.log(`\nwrote ${OUT}/results.json and screenshots to ${OUT}/`)
}

main().catch((error) => {
  console.error(error)
  process.exit(1)
})
