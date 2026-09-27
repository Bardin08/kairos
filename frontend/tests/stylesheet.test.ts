import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { expect, test } from 'vitest'

// Resolved from the working directory rather than import.meta.url: under the
// jsdom environment Vite rewrites that to an http:// URL, which is not a path.
// Vitest runs with the frontend project root as its working directory.
const stylesheet = resolve(
  process.cwd(),
  '../backend/src/Kairos.Api/wwwroot/kairos.css',
)

// The layer order is the stylesheet's whole override contract: anything the app
// adds later has to know which layer wins. If a build step ever rewrites or
// truncates the vendored file, this is what notices.
test('kairos.css declares its cascade layers in order', () => {
  const css = readFileSync(stylesheet, 'utf8')
  expect(css).toContain(
    '@layer reset, tokens, base, layout, components, states, utilities;',
  )
})

const ROW_INVALID_INPUT = '.field-row[data-invalid="true"] .input'

// Task 7 moved `data-invalid` from `.field` onto the `.field-row`, and the
// border-color and focus rules below — written for `.field` alone — stopped
// matching with no test failing: an invalid limit still rendered `.field-error`
// while the input beside it kept its ordinary border and focus ring.
test('the invalid-state border and focus ring cover the row shape, not only the field shape', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain(
    `.field[data-invalid="true"] .input,\n  .field[data-invalid="true"] .textarea,\n  ${ROW_INVALID_INPUT} {\n    border-color: var(--status-danger-fg);`,
  )
  expect(css).toContain(
    `.field[data-invalid="true"] .input:focus-visible,\n  .field[data-invalid="true"] .textarea:focus-visible,\n  ${ROW_INVALID_INPUT}:focus-visible {\n    box-shadow: 0 0 0 3px var(--status-danger-bg);`,
  )
})

// `outline: none` on the form controls is what actually removed the ring
// DESIGN.md says is never removed; moving those selectors to :focus-visible was
// bookkeeping beside it. Comments are stripped first: prose naming the
// declaration is not the declaration.
test('no rule in the stylesheet removes the focus outline', () => {
  const css = readFileSync(stylesheet, 'utf8').replace(/\/\*[\s\S]*?\*\//g, '')

  expect(css).not.toContain('outline: none')
})

// A margin or notice in minutes can legitimately run past 999. .ctl-num's width
// and .affix's trailing reservation are derived together (see the comments beside
// each in kairos.css); this pins the two numbers that arithmetic landed on.
test('the numeric control is wide enough for four digits plus the affix reservation', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain('.ctl-num { width: 6.5rem; }')
})

// `a day` (dailyCap's affix in limitCopy.ts) is wider than `min`, the other three
// limits' affix. A single reservation sized for the shorter one is what let the
// spinner-and-affix collision happen; sizing for the wider string serves both.
test('the affix reservation is sized for `a day`, not just `min`', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain('.affix > .input { padding-inline-end: var(--space-8); }')
})

// A design system that puts its own unit text in an input's trailing zone cannot
// also let the browser paint a stepper into that same zone. Scoped to .affix
// rather than a blanket input[type=number] rule, so a plain number field
// elsewhere in the app keeps its native spinner.
test('the native number spinner is suppressed only inside .affix, not for every number input', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain(
    '.affix > input[type="number"] {\n    appearance: textfield;\n    -moz-appearance: textfield;\n  }',
  )
  expect(css).toContain(
    '.affix > input[type="number"]::-webkit-outer-spin-button,\n  .affix > input[type="number"]::-webkit-inner-spin-button {\n    -webkit-appearance: none;\n    margin: 0;\n  }',
  )
  const occurrences = css.split('input[type="number"]').length - 1
  const scopedOccurrences = css.split('.affix > input[type="number"]').length - 1
  expect(occurrences).toBe(scopedOccurrences)
})

// .slots had a cap (300px, scrolling) but no floor, so the desktop slot column —
// and the card around it — was exactly as tall as the chosen day's times: 583px
// on a scrolling 16-time day, ~431px on a 5-time day. Cap and floor are now the
// same --slots-h token, and the space below a short day is deliberately empty.
test('the slot column has a floor equal to its cap, so the card is the same height on every day', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain('min-height: var(--slots-h);')
  expect(css).toContain('max-height: var(--slots-h);')
})

// .segmented's selected style was written for aria-pressed (the duration picker),
// then extended to aria-checked (the date-state radio group), then to
// aria-current (the Links section nav, whose options are pages and therefore
// links). One rule extended to all three rather than blocks copied beside it,
// matched through the closing brace so dropping a declaration fails here too.
test('the segmented selected style covers toggle, radio and page semantics in one rule', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain(
    '.segmented button[aria-pressed="true"],\n  .segmented button[aria-checked="true"],\n  .segmented a[aria-current="page"] {\n    background: var(--surface-raised);\n    color: var(--text-primary);\n    font-weight: var(--weight-semi);\n    box-shadow: var(--shadow-1);\n  }',
  )
})

// The one thing that can go wrong silently here: a section nav that renders
// `aria-current="page"` against a stylesheet that only paints `aria-pressed` and
// `aria-checked` looks like a control with nothing selected, and no rendered test
// can see it. jsdom has no cascade, so this is the only place it is pinned.
test('a section nav link is painted by aria-current, not by a toggle attribute', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain('.segmented a[aria-current="page"]')
})

// An <a> is not a <button>: the UA centres a button's label inside the box
// min-height gives it and gives an anchor an underline instead. Measured one
// declaration at a time against the running page, each of the three moves
// something — the label to the box's top edge, or into an underline. Matched
// through the closing brace, so a fourth declaration creeping back in fails
// here: `justify-content` was one, and it centred an option inside a box that
// is already the width of its label. Not the colour either — the shared rule
// above sets that, and it outranks the base `a` tint by layer, not by this
// block.
test('a segmented option that is a link is vertically centred and unadorned like the buttons beside it', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain(
    '.segmented a {\n    display: inline-flex;\n    align-items: center;\n    text-decoration: none;\n  }',
  )
})

// Blocked is the only one of the three date states that removes availability, so
// its selection is the one departure from .segmented's neutral treatment
// (--surface-raised + shadow) into the danger palette.
test('a selected segmented option can carry a danger fill instead of the neutral one', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain(
    '.segmented button.segmented-danger[aria-pressed="true"],\n  .segmented button.segmented-danger[aria-checked="true"] {\n    background: var(--status-danger-bg);\n    color: var(--status-danger-fg);\n  }',
  )
})

// A measurement against the running app found .segmented buttons rendering at
// 36px, under DESIGN.md's tap floor (44px minimum, every viewport, no exceptions
// clause). Fixed at the base rule with the --tap token rather than a literal, so
// the duration picker, the date-state control and the section nav share one
// height — the nav's options are links, and the floor binds them the same way.
test('the segmented option height is the tap-floor token, not a literal, for links too', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain('.segmented button,\n  .segmented a {\n    min-height: var(--tap);')
})

// `.admin-main` is the admin surface's only scroll container, and this is the
// declaration that keeps that true. `.visually-hidden` is absolutely positioned,
// so with the pane static a control column's hidden heading resolves against the
// initial containing block instead: it escapes the overflow clip and stretches
// the document to 2073px against a 900px viewport, a second scrollbar nothing in
// the app scrolls back. No rendered test can guard it — jsdom has no layout.
test('the admin pane is a containing block, so a hidden heading cannot escape its scroll clip', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain('.admin-main { position: relative;')
})

// A page header is as tall as the action it carries, and 16px shorter when it
// carries none — so moving between two views whose headers disagree makes every
// row below them jump. Measured at 1440x900: 44px on Links, 28px on Personal
// links, the switch landing at y=100 and y=84. The floor is the tap token rather
// than a literal, because what it is matching is the button that sets the tall
// case. No rendered test can guard it — jsdom has no layout.
test('a page header is the tap-floor tall whether or not it carries an action', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain('min-height: var(--tap);\n    margin-bottom: var(--space-5);')
})

// jsdom performs no layout, so nothing here can assert a rendered height: a test that
// mounted Book and read offsetHeight would report 0 against any stylesheet at all. What
// is assertable is that the declarations exist, and these three are load-bearing — the
// booking panel's four states share one frame only because they agree.
//
// The bug they close: the floor used to live on `.slots`, inside SlotList, and SlotList
// is one of four branches. Changing month sets `slots` to null while the fetch is in
// flight, so the panel collapsed to the single line `Finding open times…` and sprang
// back when the times arrived — the floor never applied, because the element carrying it
// was not mounted.
test('the booking panel frame is composed from the head height and the slot height', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain('--slots-head-h:')
  expect(css).toContain('min-height: calc(var(--slots-h) + var(--slots-head-h))')
  expect(css).toContain('height: var(--slots-head-h)')
})

// A UA checkbox draws at about 13px, and the box in .check-row is set to 18, so
// the 44px DESIGN.md's Tap Floor Rule requires has to come from the label. It
// cannot come from the row, which is where this floor used to sit: measured in
// Chromium, `align-items: flex-start` leaves every item as tall as its own text,
// so the row was 1016x44 around a 71.6x21.7 label and elementFromPoint at its
// mid-right answered `div.check-row` — 22px of row that toggle nothing. On the
// label the same point answers the label, at 979x44. `flex: 1` is half of that
// and is pinned with it: without the width the height alone leaves the row's
// trailing half dead. jsdom performs no layout, so nothing rendered can see
// either declaration go.
test('every option in a checkbox list is a tap-floor target through its label, which fills the row', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain(
    '.check-row label {\n    flex: 1;\n    min-height: var(--tap);\n    padding-block: var(--space-2);',
  )
})

// The settings panel's toggle has no label of its own to grow: in a .field-row
// the label and the control sit in opposite grid tracks. So an empty label wraps
// the box and is the target, and these are the two declarations that size it.
// Measured in Chromium and WebKit — Playwright's Firefox does not launch on the
// machine this was measured on: a click 20px right of the box's centre and one
// 20px above it both toggle with these declarations, and both miss without them,
// the wrapper falling to 25x24 and 22x24.
//
// This replaces a transparent `.check-tap::after` on the input, which reached the
// same 44px on the same two engines. Two engines is all either approach was ever
// measured on, and that is the argument for the label: whether a form control
// generates a pseudo-element is the engine's business, and one that declines
// leaves an 18px target and reports nothing, while a label has a box everywhere.
// jsdom performs no layout, so nothing rendered can see either declaration go.
test('the settings toggle is a tap-floor target through the label wrapping its box', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain(
    '.check-tap {\n    display: flex;\n    align-items: center;\n    justify-content: center;\n    width: var(--tap);\n    height: var(--tap);\n    margin-inline: calc((18px - var(--tap)) / 2);',
  )
  expect(css).toContain('.check-tap input { width: 18px; height: 18px; flex: none; }')
})

// The brand mark is drawn from the accent pair and their midpoint, never from
// a hex of its own. That is what lets the dark theme's inverted pair redraw it:
// a white disc on the lightened dark accent is what the Inversion Rule forbids,
// and a mark with its own hex would keep painting one.
test('the brand mark takes every colour from the accent pair', () => {
  const css = readFileSync(stylesheet, 'utf8')

  expect(css).toContain('.mark-tile { fill: var(--accent); }')
  expect(css).toContain(
    '.mark-day { fill: color-mix(in srgb, var(--text-on-accent) 50%, var(--accent)); }',
  )
  expect(css).toContain('.mark-chosen { fill: var(--text-on-accent); }')
})
