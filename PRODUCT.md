# Product

<!-- impeccable:product-schema 1 -->

Kairos, the self-hosted booking-link scheduler. This file is the durable product truth the visual work is allowed to rely on. It lived at the vault root while Kairos had no repository; it moved here on 2026-08-16, as its own first paragraph said it would. The planning context it was extracted from stays in the vault at `efforts/kairos/` — `ground.md` for what is true, `roadmap.md` for what comes next.

## Platform

web

## Stack

**Revised 2026-08-14, superseding the entry below it.** ASP.NET Core (.NET 10) minimal API in **vertical slices**, EF Core over SQLite, single self-contained arm64 container. The front end is **React** on a modern toolchain, served as static assets by the same container. **No Razor Pages anywhere.** Every dependency on both sides is pinned to an exact version — no floating ranges, no automatic updates — because supply-chain compromise is the threat being defended against. `kairos.css` carries over unchanged; React consumes it as plain CSS with `className`, and the tokens, cascade layers and three-state theming are unaffected.

The consequence, accepted deliberately: **the booking flow now requires JavaScript.** Design verification condition 1 ("completes a booking with JavaScript disabled") is withdrawn, and progressive enhancement is no longer the working assumption. What survives from that framing and is *not* optional: the bundle is a resource budget, not a free lunch. The invitee is on a mid-range Android on mobile data, so the booking route ships the smallest bundle that can do the job, and every dependency added to it has to earn its bytes.

> **Superseded 2026-08-14:** ~~Server-rendered ASP.NET Core (.NET 10) minimal API with Razor pages, single self-contained arm64 container. No SPA. No Node toolchain. Progressive enhancement is the working assumption: the booking flow is form posts, JavaScript sharpens it and never gates it.~~ Recorded here rather than deleted, because it was confirmed and then reversed on the same day and the reversal should not look like an oversight.

## Users

Two audiences with opposite situations, and the design serves the first one first.

**The invitee.** Someone Vlad sent a link to — a mentee, a client, a collaborator. Anonymous, unauthenticated, arriving from a shortened URL pasted into an email or a chat. Very likely on a phone, on mobile data, in a timezone that is not the organizer's. They visit once, for ninety seconds, to answer one question: *when can I talk to this person?* They will never learn the interface. Nothing may require a second visit to understand.

**The operator.** Vlad, the single administrator. Logs in through Pocket ID (OIDC), on a desktop most of the time and on a phone when something needs checking away from the desk. Uses three surfaces: Links (the bookable event types), Availability (named reusable patterns plus the global margin/notice/cap/increment defaults), Meetings (every booking, past and upcoming, each row carrying the link it came through). Repeat user, high frequency, tolerant of density, intolerant of ceremony.

There is no third role. No teams, no organizations, no admin hierarchy.

## Product Purpose

Booking links that show real availability, respect the gap the operator needs between calls, cannot double-book, and can be reviewed afterwards. Success is a booking that lands at the correct wall-clock time in both calendars, made without a single message exchanged.

## Positioning

Built because Calendly's self-hosted equivalent costs 1.2 Gi of RAM and drags PostgreSQL into a deliberately SQLite-only cluster. Kairos targets ~120 Mi steady, SQLite plus Litestream, one container. The mechanism a neighbouring product cannot truthfully copy: **margins are part of the slot algebra, not a setting layered on top.** A booking occupies its duration plus the larger of the two adjacent links' margins; slot generation is interval arithmetic, so notice, daily cap and start increments are parameters rather than special cases. The visible consequence is that the operator's recovery time between calls is structurally protected, on every link at once.

## Operating Context

- Links are handed out through a URL shortener, so an invitee usually arrives via a redirect and has no idea what Kairos is.
- Booking pages are public and bypass Cloudflare Access; the admin surface sits behind it. One route-level split.
- Timezones are load-bearing and adversarial: availability is wall-clock local plus an IANA zone, materialized per request. DST changes are a correctness event, not an edge case.
- Every link draws from one shared conflict pool, plus external Google Calendar busy blocks. A slot taken on one link disappears from all of them.
- The whole thing runs on a Raspberry Pi 5 in a k3s cluster. Resource limits are 192Mi/500m.

## Capabilities and Constraints

**Confirmed in scope (phase 1):** multiple event types each with its own link and availability window; reusable named availability patterns; correct timezone and DST handling for both sides; margins, minimum notice, daily booking cap, constrained start increments; global conflict pool plus external busy check; Google Meet link created on booking; confirmation email with a working `.ics`; self-serve cancel and reschedule via signed tokens; Meetings list with source link per row; OIDC admin login; a narrow REST surface plus booking webhooks.

**Later phases:** prefilled single-use personal links, custom booking questions, email reminders, Zoom (phase 3); recurring bookings as an RFC 5545 series (phase 4); post-meeting workflows (phase 5, conditional).

**Out, permanently:** traffic analytics of any kind — the shortener owns clicks, Kairos owns outcomes, and two disagreeing counters are worse than one. Also out: teams, round-robin, group events, payments, routing forms, polls, embeds, SMS, browser extension, SAML, SCIM, and PostgreSQL.

**Duration is a free per-link value, confirmed 2026-08-14.** Any whole number of minutes, configured on the link, not chosen from a menu of 15/30/45/60. A 25-minute call and a 50-minute call are ordinary cases, not exceptions — they are how an operator keeps a real gap without spending the whole hour. Consequences the slot generator must carry: the end time is arithmetic and often lands off the half hour; the interval a booking occupies is duration plus the resolved margin; and the next legal start is the following start-increment boundary after that, not the moment the previous meeting ends. No surface may hard-code a duration or assume a round one.

**Terminology, fixed:** *Link* = a bookable event type. *Pattern* = a named reusable availability schedule. *Meeting* = a booking that exists. *Margin* = the required gap between two meetings. These four words appear in the UI exactly as written; they were confused repeatedly in discussion and the naming is now settled.

**Undecided:** whether Meetings shows only app-created bookings or reads the calendar back; which note-taker (blocks phase 5); how far a recurring series runs.

## Brand Commitments

No logo, no wordmark, no palette, no typeface, and no requirement to match the other homelab services. The name *Kairos* — Greek, the opportune moment as opposed to chronological time — is the only fixed asset, and its meaning is not binding on the design.

**Standing preference, recorded 2026-08-14.** After two re-rolls through assigned and challenger visual worlds, the user chose convention deliberately: *"I want more canonical experience but with a modern hand rather than some outstanding design that users won't understand."* Kairos ships the arrangement its audience already knows — the familiar booking-page and admin-shell layouts — executed at a named craft bar rather than dressed in a foreign visual world. This is a durable commitment, not a one-off: future surfaces inherit it and do not re-open the world question.

**The craft bar, named by the user:** Linear, Stripe (Checkout and Dashboard), Notion Calendar, and Apple's own iOS system apps. Their *standard of finish* is the target — restraint, state clarity, form design, mobile behaviour, measured motion — never their visual identity copied.

## Evidence on Hand

No customers, no testimonials, no benchmarks, no pricing, no screenshots of a running system. Nothing is deployed. The measured figures that may be stated are the ones in `efforts/scheduler/scheduler.md`: cal.diy at ~1.2 Gi steady with PostgreSQL, Kairos targeting ~120 Mi with SQLite. Everything else shown in a mockup is authored demonstration material and must be labelled as such. Invitee names, emails, and meeting subjects in any comp are invented.

## Product Principles

1. **The invitee never learns the interface.** Ninety seconds, one visit, on a phone. Anything that requires orientation has failed before it is judged on taste.
2. **Time is data, and it must be unambiguous.** Which slot is selected, in which zone, for how long — never inferable, always stated. Tabular figures, explicit timezone, no relative-date cleverness.
3. **The gap between meetings is a feature, not a setting.** Margins, notice and caps are the reason this exists; the operator's surfaces should make them visible rather than bury them three panels deep.
4. **Weight is a correctness property.** 192Mi and a Pi 5 upstream; a mid-range Android on mobile data downstream. Effects that cost frames are wrong here regardless of how they look.
5. **One operator, no ceremony.** No onboarding, no empty-state coaching, no dashboards of numbers nobody acts on. Admin is a workbench.

## Accessibility & Inclusion

No formal compliance target (user's decision, 2026-08-14). Working floor, because the public page cannot know who opens it: keyboard reachable throughout, visible focus, readable contrast, 44px minimum touch targets, and `prefers-reduced-motion` honoured. ~~The booking flow usable without JavaScript~~ — dropped 2026-08-14 with the move to React. Everything else in the floor stands, and the React implementation carries the burden of keeping it: focus management and announcement are things a server-rendered page got for free and an SPA has to author.

## Explicit rejection criteria

Stated by the user 2026-08-14 as what would make a technically polished result feel wrong:

- **Too clever to use.** A distinctive world that makes an invitee hesitate about which slot is selected or where to tap. Expression beating the task is the failure.
- **Decorative motion.** Animation that exists to be seen rather than to orient. Nothing may slow down picking a slot.
- **Heavy or slow on a phone.** Blur, shadow stacks, large assets, layout jank. It must feel instant on a mid-range Android over mobile data.

Notably *not* rejected: looking like a normal SaaS app. Distinctiveness is welcome but it is not the axis the user is defending.
