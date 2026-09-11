---
name: Kairos
description: Self-hosted booking links — a public booking page and a single-operator workbench.
colors:
  ink: "#171A20"
  ink-muted: "#545A69"
  ink-faint: "#6E7484"
  paper: "#FFFFFF"
  paper-sunken: "#F6F7F9"
  paper-inset: "#EDEEF2"
  hairline: "#E1E3EA"
  hairline-faint: "#EDEEF2"
  signal: "#4E56E3"
  signal-deep: "#3E44C4"
  signal-wash: "#EEF0FE"
  night: "#0E1014"
  night-raised: "#171A20"
  night-signal: "#737DEE"
  night-on-signal: "#0B0D2B"
  confirm: "#168A5A"
  refuse: "#C4362C"
typography:
  sans:
    fontFamily: "'Kairos Sans', 'Kairos Sans Fallback Apple', 'Kairos Sans Fallback', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, 'Noto Sans', sans-serif"
  display:
    fontFamily: "'Kairos Display', 'Kairos Display Fallback', {typography.sans.fontFamily}"
    fontSize: "2rem"
    fontWeight: 400
    lineHeight: 1.15
    letterSpacing: "-0.015em"
  headline:
    fontFamily: "{typography.sans.fontFamily}"
    fontSize: "1.5rem"
    fontWeight: 600
    lineHeight: 1.15
    letterSpacing: "-0.022em"
  title:
    fontFamily: "{typography.sans.fontFamily}"
    fontSize: "1.25rem"
    fontWeight: 600
    lineHeight: 1.35
    letterSpacing: "-0.012em"
  body:
    fontFamily: "{typography.sans.fontFamily}"
    fontSize: "1rem"
    fontWeight: 400
    lineHeight: 1.55
    letterSpacing: "-0.012em"
  ui:
    fontFamily: "{typography.sans.fontFamily}"
    fontSize: "0.875rem"
    fontWeight: 500
    lineHeight: 1.55
    letterSpacing: "-0.012em"
  label:
    fontFamily: "{typography.sans.fontFamily}"
    fontSize: "0.75rem"
    fontWeight: 600
    lineHeight: 1.35
    letterSpacing: "0.06em"
  numeric:
    fontFamily: "{typography.sans.fontFamily}"
    fontVariation: "tabular-nums"
rounded:
  xs: "4px"
  sm: "6px"
  md: "8px"
  lg: "12px"
  xl: "16px"
  full: "999px"
spacing:
  "1": "4px"
  "2": "8px"
  "3": "12px"
  "4": "16px"
  "5": "24px"
  "6": "32px"
  "7": "40px"
  "8": "48px"
  "9": "64px"
  "10": "96px"
components:
  button-primary:
    backgroundColor: "{colors.signal}"
    textColor: "{colors.paper}"
    typography: "{typography.ui}"
    rounded: "{rounded.md}"
    padding: "0 16px"
    height: "44px"
  button-primary-hover:
    backgroundColor: "{colors.signal-deep}"
    textColor: "{colors.paper}"
  button-secondary:
    backgroundColor: "{colors.paper}"
    textColor: "{colors.ink}"
    typography: "{typography.ui}"
    rounded: "{rounded.md}"
    padding: "0 16px"
    height: "44px"
  button-ghost:
    backgroundColor: "transparent"
    textColor: "{colors.ink}"
    typography: "{typography.ui}"
    rounded: "{rounded.md}"
    height: "44px"
  slot:
    backgroundColor: "{colors.paper}"
    textColor: "{colors.ink}"
    typography: "{typography.ui}"
    rounded: "{rounded.md}"
    height: "44px"
  slot-selected:
    backgroundColor: "{colors.signal}"
    textColor: "{colors.paper}"
    typography: "{typography.ui}"
    rounded: "{rounded.md}"
    height: "44px"
  day-selected:
    backgroundColor: "{colors.signal}"
    textColor: "{colors.paper}"
    rounded: "{rounded.full}"
    size: "40px"
  input:
    backgroundColor: "{colors.paper}"
    textColor: "{colors.ink}"
    typography: "{typography.body}"
    rounded: "{rounded.md}"
    padding: "12px"
    height: "44px"
  card:
    backgroundColor: "{colors.paper}"
    textColor: "{colors.ink}"
    rounded: "{rounded.xl}"
    padding: "32px"
  badge:
    backgroundColor: "{colors.paper-sunken}"
    textColor: "{colors.ink-muted}"
    typography: "{typography.label}"
    rounded: "{rounded.full}"
    padding: "3px 12px"
---

# Design System: Kairos

## Overview

**Creative North Star: "The Departure Board"**

A departure board exists for one reason: a stranger, under time pressure, in bad light, has to become certain of one thing. It does not decorate. Everything on it is either a time, a destination, or the mark showing which row is yours. Kairos is built to that standard. An invitee arrives from a link someone pasted into a message, on a phone, with ninety seconds of attention and no intention of ever learning the interface. They need to see which times are real, understand which one they have selected, and leave.

The system is therefore quiet on purpose, and its restraint is not timidity — it is the shape of the job. There is one accent colour, and it is spent on three things: the time the invitee has selected, the action that commits it, and the current tab in the operator's phone tab bar. Every other surface is a cool neutral, separated by hairlines rather than by shadow. Type is Source Sans 3, self-hosted and subset so that an English invitee pays 28.7 KB for it and a Polish one pays for the accents only when a Polish name is actually on the page. The single place typography is load-bearing is numerals, and it is treated that way: figures come from a 2.3 KB digits-only cut of IBM Plex Mono, scoped by `unicode-range` so it can lead the global stack while every letter behind it stays Source Sans. A time on a departure board is not the same kind of thing as a word, and here it does not look like one.

The same vocabulary carries the operator's workbench without a second design. Density rises — tables, patterns, records — but nothing new is introduced: the same ramp, the same hairlines, the same accent discipline, the same figures. A screen that needs a device this system does not have is usually a screen that has misunderstood the task.

**Key Characteristics:**
- One accent, spent only on the selection, the primary action and the current tab
- Hairlines and tonal layering carry structure; shadow is reserved for what floats
- Tabular figures everywhere a number appears
- 44px minimum touch target on every viewport, not only touch ones
- State is never carried by colour alone
- One authored motion moment in the entire product

## Colors

Cool neutrals at a single hue with one saturated accent; nothing else is coloured except status, and status is always double-coded.

### Primary
- **Signal Indigo** (`{colors.signal}`): the selected time, the selected day, the primary action, and the current tab in the phone tab bar. Nothing else — those are the three jobs the rule below names, and a fourth kind of element wearing this colour is a defect, not a variation. In dark mode it lightens to `{colors.night-signal}`, which inverts what can legibly sit on it — labels there are `{colors.night-on-signal}`, not white.
- **Signal Wash** (`{colors.signal-wash}`): the confirmation summary panel and the avatar, where the accent needs to be present without competing with the selection.

### Neutral
- **Ink** (`{colors.ink}`): primary text and every heading.
- **Muted Ink** (`{colors.ink-muted}`): supporting copy, table values, the facts beside the event title.
- **Faint Ink** (`{colors.ink-faint}`): counts, timestamps, column names, fine print.
- **Paper** (`{colors.paper}`): the page and every raised surface in light mode. In dark mode the page is **Night** (`{colors.night}`) and raised surfaces lift to `{colors.night-raised}`.
- **Sunken Paper** (`{colors.paper-sunken}`): table headers, the admin rail, inset chips — recessed rather than raised.
- **Hairline** (`{colors.hairline}`): every border that separates two things. `{colors.hairline-faint}` separates rows inside one thing.

### Tertiary
- **Confirm Green** (`{colors.confirm}`) and **Refuse Red** (`{colors.refuse}`): status and errors only. Never a decorative accent, never a chart colour.

### Named Rules

**The Two Jobs Rule.** The accent has exactly three jobs: mark the selected time, mark the action that commits it, and mark where you are — which on this product means the current tab in the phone tab bar, and nothing else that navigates. The rail marks its own current destination with an inset fill and heavier weight rather than with the accent, and that is not a gap to be filled. The wordmark is the single exemption: a brand mark is not a control and holds no state, so it takes no job at all. The third job was decided 2026-08-16, after a measurement read the tab bar as a violation and the Navigation section below turned out to be the document that was right; this rule was rewritten to say so on 2026-08-23. The code changed on neither date. A hover state, a link, a badge, an icon or a heading tinted with the accent has still taken a job that is not its own. The name stays: the rule is known by it, and a rule nobody can cite by name is a rule nobody cites.

Four uses that predate the third job were judged against the amended wording on 2026-08-23 rather than assumed compliant. Each gets a verdict, because "it has always looked like that" is not one:

- **`.wordmark-glyph` — exempt, and identity is an exemption rather than a fourth job.** A job is something the accent does to a control the reader can act on; the mark is the one place the colour is the brand rather than a signal. Nothing acquires the exemption by resembling it — an icon tinted to look like the mark is still an icon tinted with the accent.
- **`.pattern-day[data-on="true"]` — compliant, job one at the scale a pattern is edited in.** The weekday chips say which days a pattern opens, which is the selected time seen a week at a time rather than a slot at a time, and they say it in the wash, which the palette already defines as the register where the accent is present without competing with a selection. The chips are `aria-hidden` and the same days are named in words on the line below, so the fill is a picture of a fact stated elsewhere and the Second Channel Rule holds too.
- **`a { color: var(--accent-text) }` — violation, not closed here.** Argued under *Open against the Two Jobs Rule* at the end of this document, with what closing it costs.
- **`.slot:hover` — violation, not closed here.** Same section, same reason: the declaration is one line and the decision behind it is not.

**The Second Channel Rule.** No state is carried by hue alone. An available day carries a dot as well as its ink; a live link carries a filled dot and a paused one carries a hollow dot of the same size; an invalid field carries a message and an icon, not just a red border. Every state must survive a monochrome screenshot.

**The Inversion Rule.** Dark mode is not the light palette with the values flipped. The accent lightens so it holds against a near-black ground, and because it lightens, what sits on top of it must darken. White on the dark-mode accent measures ~3.5:1 and is prohibited.

## Typography

**Body & UI:** Source Sans 3, self-hosted, variable weight axis (`Kairos Sans`)
**Display:** Source Serif 4 at 400, self-hosted, pinned to one weight (`Kairos Display`) — the booking page's `<h1>` and nothing else
**Numeric:** IBM Plex Mono, subset to digits and time punctuation (`Kairos Figures`), leading `--font-numeric` and falling through to the sans

**Character:** warm humanist rather than neutral grotesque. An invitee is a stranger who arrived from a link someone pasted at them, and a face with a little warmth in it is doing work that a colder, more corporate one does not. The serif appears exactly once, on the one line of the booking page that is a name rather than a control, and the mono appears only on figures — which on this product means the thing the whole page exists to show.

This supersedes the system stack. That was chosen under two constraints, and one of them is gone: the no-Node-toolchain rule was withdrawn when the front end became React. The other — a mid-range Android on mobile data — still binds, and it is why the type ships the way it does rather than why it does not ship at all.

### What it costs, and how that is held

31.0 KB blocks first paint: Source Sans 3 latin (28,740) and the digits-only Plex Mono subset (2,260), both `rel="preload"`ed ahead of the stylesheet so they start in the first round trip instead of the third. Source Serif 4 (18,708) is deliberately **not** preloaded — it is one heading, and its metric-matched fallback means arriving late costs no layout shift.

Nothing is shipped whole. Each family is split by `unicode-range`, so an English invitee downloads latin and nothing else; `latin-ext` arrives only when a Polish name is on the page, `cyrillic` only when a Ukrainian one is. This is not hypothetical tidiness — measured across the five candidate faces, the latin subset carries `ü` but not `ę ł ś ć ż`, so without the split those names fall back mid-word and change width.

`scripts/bundle-budget.sh` enforces 32 KiB on the preloaded fonts, as a number separate from the code budget. Fonts are weighed raw rather than gzipped, because WOFF2 is already Brotli-compressed and gzipping it again reports a figure nobody downloads. Two budgets rather than one total, so a JS regression cannot hide inside font headroom.

Every `size-adjust` and `*-override` in the FACES block of `kairos.css` is computed from the real font tables, not estimated. Regenerate with `scripts/font-metrics.py` whenever a face changes.

### Hierarchy
- **Display** (Source Serif 4, 400, 2rem, 1.15, -0.015em): the event title on the booking page. One per page, and the only display type in the product.
- **Headline** (600, 1.5rem, 1.15): admin page titles.
- **Title** (600, 1.25rem, 1.35): section headings inside a card or pane.
- **Body** (400, 1rem, 1.55): invitee-facing prose and every form control. Never smaller — this is read once, on a phone, by a stranger. Measure caps at 68ch.
- **UI** (500, 0.875rem, 1.55): the admin default, buttons, table cells, times.
- **Label** (600, 0.75rem, 0.06em, uppercase): column names and field legends only.

### Named Rules

**The Tabular Rule.** Anything that is a time, a date, a duration or a count sets in tabular figures. A slot column whose digits change width is a column that jitters when the day changes.

**The One Declaration Rule.** `--font-numeric` is set on `body`, not on a list of components. The `unicode-range` on `Kairos Figures` already restricts that face to digits, so one declaration covers every figure in the product and cannot be forgotten on a component written next year. A rule that names components is a rule with a maintenance cost; this one has none.

**The No Eyebrow Rule.** No kicker or eyebrow above a heading, anywhere, ever. A heading that needs a label above it to be understood needs a better heading.

**The Invitee Floor Rule.** No text an invitee must read is set below 1rem. The admin may go to 0.875rem because its reader uses it daily; the stranger gets no such practice.

## Layout

A 4px base scale (`{spacing.1}`–`{spacing.10}`), applied with rhythm rather than uniformly: related lines inside a card sit at 12px, sections break at 64px, and a heading always gets more space above it than below.

The booking page is one card with two panes — details and calendar — collapsing to a single column at **860px**. Below that breakpoint the primary action leaves the card and becomes a sticky bar at the bottom edge, inside thumb reach, stating the choice in full rather than only offering the verb. The month is a fixed-width object capped at 300px, never a stretchy column; letting it take a free fraction is what turns a calendar into a wall of tiles.

The admin shell is a 240px rail plus content, collapsing at **900px** to a bottom tab bar carrying the same three destinations, with Settings as a row of its own above them (see Navigation). Tables become lists of records at **760px**, each cell keeping its column name so a value is never orphaned from its meaning.

Breakpoints: 640 (page padding and card insets), 760 (tables to records), 860 (booking panes, sticky action bar), 900 (admin rail to tab bar).

### Named Rules

**The Tap Floor Rule.** 44px minimum on every interactive target, on every viewport. A laptop trackpad misses small targets too.

**The Thumb Rule.** On a phone, the action that commits sits at the bottom edge, above the safe-area inset, and names what it will do.

## Elevation & Depth

Predominantly flat. Structure comes from hairline borders and tonal layering — sunken, page, raised — and shadow is reserved for things that genuinely float above the page. Two levels are in real use; a third exists for the sticky bars.

### Shadow Vocabulary
- **Resting** (`0 1px 2px rgba(23,26,32,.06), 0 1px 1px rgba(23,26,32,.04)`): cards and the primary button. Barely present; it separates a surface from the page without implying lift.
- **Raised** (`0 2px 4px rgba(23,26,32,.05), 0 8px 16px -6px rgba(23,26,32,.10)`): the booking card, which is the page's subject.
- **Floating** (`0 4px 8px rgba(23,26,32,.06), 0 20px 32px -12px rgba(23,26,32,.16)`): the sticky action bar only.

### Named Rules

**The Offset Rule.** Every shadow carries an offset and a soft blur. A zero-offset halo is decoration wearing depth's clothes and is prohibited.

## Shapes

Gently rounded, never soft: 8px (`{rounded.md}`) is the default for controls, 12–16px for cards, and 6px for small inset chips. One shape breaks the pattern deliberately — a selected day is a **full circle** (`{rounded.full}`), because a calendar day is a point in time and reads wrong as a rounded rectangle. Borders are always 1px; a coloured border wider than 1px does not exist in this system.

## Components

### Buttons
- **Shape:** gently rounded (8px), 44px tall, horizontal padding 16px.
- **Primary:** accent fill, white label in light mode and near-black indigo in dark, resting shadow. One per view.
- **Secondary:** paper fill, hairline border, ink label.
- **Ghost:** transparent, ink label, used for retreat actions (Back, Reschedule).
- **Muted:** transparent, secondary-tone label — for an action that is neither a retreat, a selection, nor the primary one (Add window).
- **Hover / active:** background shift at 90ms; a 0.985 scale on press so the control feels physical.
- **Loading:** the label stays and the spinner overlays it, so the control does not resize and the reader does not lose it.
- **Disabled:** inset fill, disabled ink, no shadow, `cursor: not-allowed`.

### Cards / Containers
- **Corner:** 12px, or 16px for a page's subject.
- **Background:** raised surface; **border:** 1px hairline; **shadow:** resting, or raised for the subject.
- **Padding:** 32px, dropping to 24px below 640px.
- Cards never nest.

### Inputs / Fields
- **Style:** raised fill, 1px hairline, 8px radius, 44px minimum, 1rem text.
- **Focus:** accent border plus a 3px accent-wash ring. The ring is never removed, only inset where a container would clip it.
- **Error:** red border, and a message under the control that names the problem *and* the recovery — never a banner at the top of the form.

### Navigation
- **Desktop:** a rail of three destinations, active state as a filled inset with heavier weight.
- **Phone:** a bottom tab bar, the same three destinations, accent used only for the current one.
- **Settings:** the fourth surface, and deliberately not a fourth destination. The organizer's timezone, the four global limit defaults and the way through to Integrations have to live somewhere, and they are visited rarely, so Settings sits under the hairline at the foot of the rail, and on a phone as a single row at the tap floor above the tab grid, with a hairline under it saying it is not one of the three. The grid stays three equal columns — a fourth child drops into a ragged second row a third of the way across, so the layout says "three" as loudly as the design does.

### Slot grid (signature)
The product's defining component. Times render as a 2-column grid on a phone and a scrolling 2-column column on desktop, each a 44px target in tabular figures. Selection is `aria-pressed`, painted with the accent. Availability is generated, not listed: a candidate `[start, start + duration)` is tested against each existing meeting padded by the resolved margin, with starts advancing on the increment grid. The margin pads the busy interval, never the candidate — padding both yields the sum of two margins instead of the larger of them.

### Duration picker (signature)
Present only when a link offers more than one duration; a single-duration link states its duration as text, because a control with one option is furniture. Changing the duration recomputes the month as well as the list — a day with a 40-minute gap is available for 25 minutes and unavailable for 50.

### Month grid
A day is a number, not a tile. The 44px cell is a hit area; paint appears only on hover or selection, as a 40px disc. Unavailable days stay in place, dimmed, rather than vanishing — an invitee needs to see that a month is mostly full, not be handed a calendar with holes punched in it.

## Do's and Don'ts

### Do:
- **Do** spend the accent only on the selected time, the action that commits it, and the tab the operator is currently on.
- **Do** set every time, date, duration and count in tabular figures.
- **Do** give every state a second channel — a dot, a shape, a weight, a message.
- **Do** keep 44px targets everywhere, and put the committing action at the bottom edge on phones.
- **Do** state what the invitee is committing to, in their own timezone, before the button that commits it.
- **Do** name the reason in an empty state: which constraint closed the day, not just that it is closed.
- **Do** theme the surfaces nobody draws — selection, caret, scrollbars, focus rings.

### Don't:
- **Don't** put a kicker or eyebrow above a heading.
- **Don't** carry a state in colour alone, and never put white on the dark-mode accent.
- **Don't** let the month grid stretch to a free fraction; it is a fixed object, capped at 300px.
- **Don't** ship a hover style on a selection-shaped control without gating it behind `@media (hover: hover)` — on touch it sticks after the tap and reads as a second selection.
- **Don't** set layout in JavaScript. Composition belongs in a media query and must survive with JS off.
- **Don't** add a second entrance animation. There is exactly one authored motion moment: the slot list re-staggering when the day or duration changes, 14ms per item on an exponential ease-out. Everything else is state feedback under 140ms.
- **Don't** nest cards, and don't reach for a modal for a task that needs neither interruption nor protected focus.

## Open against the Two Jobs Rule

Two accent uses were judged violations on 2026-08-23 and deliberately left standing by the pull request that judged them: in both the declaration is one line and the decision behind it is not. They are listed here as well as in the verdicts under the rule, because a follow-up that lives only inside a paragraph is a follow-up nobody schedules.

- **`kairos.css:526` — `a { color: var(--accent-text) }`.** The rule names a link. Closing it means deciding what a link looks like once it is not accent-tinted — ink, with the underline it already carries doing the work — and reading that on a real page in both themes before it is typed, because the declaration governs every link the product will ever have rather than the one inline prose anchor it has today — `Integrations.tsx:350`. The other two anchors wear `.btn`, which sets its own `--btn-fg` and never sees the tint, which is why the exposure looked smaller than the decision is.
- **`kairos.css:1123` — `.slot:hover`.** The rule names a hover state, and this one is on the single control where the accent already means *chosen*. The defence — that a wash on hover is job one at lower intensity — is exactly the ambiguity the rule exists to prevent, on the one element that cannot afford it. Closing it means moving the hover into the neutral ramp and then showing that hover, selected and unavailable are still three legibly different things at a 44px target in both themes. That is a look at the signature component, not a token swap.
The sidecar entry that used to sit here — `.impeccable/design.json` still saying two — is closed. It was corrected by hand rather than regenerated: `document` rewrites the whole sidecar, including the component snippets and the ramps nothing had asked to change, and five strings edited in place is the smaller diff to read. Those five are the overview paragraph, the key characteristic, the rule body, the do, and the slot component's description, all now in the wording this document uses. The overview's other stale claim went with them — it still described the type as a system stack, which Typography superseded.
