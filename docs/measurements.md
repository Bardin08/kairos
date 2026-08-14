# Measurements

What Part A actually costs, and the evidence that its eleven acceptance conditions hold. Every number here was measured rather than estimated; where a measurement disagreed with an earlier one, the measurement is what is written down.

Measured 2026-08-14 on macOS 27 / arm64, .NET 10.0.101, framework-dependent Release publish. Reproducing this on the Raspberry Pi the app is meant to live on will move the memory figure and nothing else — see condition 10.

Conditions 2, 3, 4, 7, 9, 10 and 11 were measured against `99c874a`. Conditions 1, 5, 6 and 8 were re-measured against `223079b`, which changed how the booking page derives dates and grew the bundle; the figures below are the later ones.

## Headline

```
bundle          69.1 kB gzipped   (budget 76.8 kB)
allocations      8.2 kB per month of slots   (budget 65.5 kB)
idle RSS       159.2 MiB          (target under 100 Mi — MISSED, see below)
verify.sh       16 checks, 0 failed
```

## Bundle

`bash scripts/bundle-budget.sh` is the only place the budget number lives, and it weighs everything the build writes into `wwwroot/assets/` — JavaScript and CSS alike.

```
bundle-budget: 69107 bytes gzipped across 1 file(s), budget 76800
```

One file, because Vite emitted no CSS chunk: Kairos's stylesheet is the vendored `wwwroot/kairos.css`, which the API serves directly and which no build measures. 7,693 bytes of headroom. The React 19.2 shell alone was 61,641, so the two real pages, four components and three helpers cost roughly 7.5 kB gzipped between them.

It was 68,789 at `99c874a`. The 318 bytes came from `223079b`, which added `zonedOffset()` and moved the booker's form state up into `Book`.

## Allocations

The slot walk allocates once for the result list and not at all for the candidates. `AllocationTests` asserts the ceiling; the figures below come from running the same `Measure` against the published `SlotGenerator`, because the test asserts a bound rather than printing what it saw.

```
30 days, 09:00–17:00, 30-minute increment    8,240 bytes
31 days, 08:00–20:00, 15-minute increment   24,384 bytes
budget                                      65,536 bytes
```

The budget scales with candidates, not with days. The second row is the one that matters for headroom: a twelve-hour day at a quarter-hour increment is three times the work and is still at 37% of the ceiling.

## Memory

Measured on a freshly started Release process with its own SQLite file, after loading both booking pages in a real browser and idling 30 seconds.

```
ps rss                        159.2 MiB
macOS physical footprint      119.8 MB
  of which dirty, untagged     95.7 MB   (GC heap, JIT code, loader heaps)
  of which malloc              18.2 MB
```

That misses the "under 100 Mi" target. Before reading it as a Kairos regression, note the floor underneath it: an empty `WebApplication.CreateSlimBuilder` app with one `MapGet`, published the same way and run on the same machine, measures **80.5 MiB RSS / 52.5 MB footprint**. Four fifths of the target is gone before any Kairos code runs. Kairos's own share is about 79 MiB RSS / 67 MB footprint on top of that floor.

The target was written for the Raspberry Pi this is meant to be deployed to — linux-arm64, 4 KiB pages, a different runtime baseline entirely. It has not been measured there, and this figure is not a substitute for doing so.

## The acceptance suite

`bash scripts/verify.sh`, run from the repository root:

```
16 passed, 0 skipped, 0 failed
```

Every check that skipped in Task 1 for want of a frontend now runs: lints, typechecks, tests, bundle under budget. The backend suite inside it is 87 tests, 0 failed.

## The eleven conditions

Run against a private instance on port 5300 with its own database, seeded by SQL with two links — `consulting` (durations 25 and 50, margin 30, notice 120, cap 4, increment 30) and `mentorship` (single duration 30, margin 0, everything else inherited) — over a Mon–Fri 09:00–17:00 pattern, organizer zone `Europe/Warsaw`. Browser conditions used headless Chromium via Playwright 1.62.1 with an explicit `timezoneId`.

### 1. Cross-timezone across DST — pass

Browser `America/New_York`, organizer `Europe/Warsaw`, booking on Monday 26 October 2026 — the Monday after Poland falls back and the week before New York does, so the usual six-hour gap is five.

The organizer's 09:00 rendered as **04:00** in New York, and the control a week earlier pins the shift:

```
19 Oct (Warsaw still CEST)  first three slots, rendered in New York:  03:00  03:30  04:00
26 Oct (Warsaw now CET)     first three slots, rendered in New York:  04:00  04:30  05:00
```

Booked end to end through the page. The confirmation read `Monday 26 October, 04:00–04:25 · America/New_York`; the row stored is `2026-10-26 08:00:00`–`08:25:00` UTC, which is 09:00–09:25 Warsaw and 04:00–04:25 New York.

**No slot in this range falls in a DST-repeated hour**, so nothing here carries an offset suffix. Warsaw's repeated hour is 02:00–03:00 on Sunday 25 October, and the pattern has no Sunday rule. Three further things the same re-run pinned down:

- The month grid opens on the **organizer's** current month — `August 2026` with the invitee in `America/New_York` — and *Previous month* is disabled there.
- Every surface names the same moment in the invitee's zone. With the invitee in `Asia/Tokyo`, where the organizer's last slot crosses midnight, the slot button reads `00:30 +1d`, the desktop line reads `Tuesday 27 October, 00:30–00:55` and the compact bar reads `Tue 27 Oct, 00:30`, while the column heading stays `Monday 26 October` — which is the organizer's day, and is the one thing on screen that is supposed to be. The two CTA surfaces are mutually exclusive: desktop at 1600px, bar at 420px, never both.
- Ambiguous times are disambiguated. The slot walk emits each wall-clock time once, so an organizer in Warsaw never sees a collision on Warsaw's own fall-back day; the collision belongs to the invitee. With the invitee in `America/New_York` on 1 November — the day New York falls back — and the organizer offering 06:00–08:00 Warsaw, the column renders `01:00 GMT-4  01:30 GMT-4  01:00 GMT-5  01:30 GMT-5` rather than two identical pairs.

### 2. Cross-link margin — pass

`consulting` booked 10:00–10:50 Warsaw (margin 30, so the shadow is 09:30–11:20). `mentorship`, margin 0, then offered on the same date:

```
09:00  11:30  12:00  12:30  13:00  13:30  14:00  14:30  15:00  15:30  16:00  16:30
```

**11:00 absent, 11:30 present.** 09:00 survives because a 30-minute meeting there ends exactly at 09:30, where the shadow begins — the interval is half-open and behaves like it. 11:15 is absent too, but that proves nothing: `mentorship` inherits a 30-minute start increment, so 11:15 was never a candidate on any day.

### 3. Concurrency — pass

Two POSTs for `2026-09-08T07:00:00Z`, released together off a thread barrier:

```
thread 0: HTTP 201  01a0021d-637f-714f-a98b-1f53cdb91e40
thread 1: HTTP 409  {"code":"slot_taken", ...}
SELECT COUNT(*) FROM Bookings WHERE StartUtc = ... -> 1
```

201 rather than 200: the create endpoint answers `Created` with a `Location` header.

### 4. Cancel frees everywhere — pass

Cancelled the 10:00 booking through the manage page in a browser — "Cancel meeting", then "Yes, cancel it" — which left the page reading *Cancelled* and *the time is back on the organizer's calendar*. Both booking links then reloaded with the slot **and its margin shadow** restored:

```
before, mentorship:   09:00                             11:30 12:00 …
after,  mentorship:   09:00 09:30 10:00 10:30 11:00 11:30 12:00 …
after,  consulting @25: full 16-slot day, 09:00 … 16:30
```

### 5. Duration changes the month — pass

`consulting`, September 2026, switching 25 → 50:

```
times   24 Sep @25: 09:00 … 16:30  (16 starts)
        24 Sep @50: 09:00 … 16:00  (15 starts — a 50 cannot start at 16:30 in a window ending 17:00)
days    open @25: 1,2,3,4,7,8,9,10,11,14,15,16,17,18,21,22,23,24,25,28,29,30
        open @50: 1,2,3,4,7,8,9,10,11,14,15,16,17,18,21,22,23,24,   28,29,30
        closed by the switch: 25 September
```

The day set only moves if some day is too short for the longer meeting, which a uniform 09:00–17:00 week never is. 25 September therefore carries a `PatternOverride` of 09:00–09:45: a 25 fits at 09:00, a 50 does not fit at all, and the square goes from available to unavailable when the duration changes. That override is part of the fixture, not part of the seed.

### 6. Theme — pass, with one thing not exercised

First-frame values captured in a `requestAnimationFrame` callback registered before any page script — rAF runs before that frame's paint, so what it reads is what was about to be drawn.

```
nothing stored, OS light   first frame: attr=null   --surface-page #FFFFFF
nothing stored, OS dark    first frame: attr=null   --surface-page #0E1014
stored dark,   OS light    first frame: attr=dark   --surface-page #0E1014
stored light,  OS dark     first frame: attr=light  --surface-page #FFFFFF
```

No flash in any of the four: the first frame already carried the settled value, including the case where the stored choice contradicts the OS. Choosing "system" removed the attribute and the stored key, the page returned to the dark OS palette immediately, and a reload kept it there.

Not exercised: **Part A ships no theme control.** `src/lib/theme.ts` is covered by unit tests but nothing in the app imports it, so the explicit-choice half above was driven through the storage-and-attribute contract that `applyChoice` and the pre-paint script in `index.html` share, not by clicking anything.

### 7. Identifiers — pass

No sequential integer appears in any URL. The complete route inventory is `/links`, `/links/{slug}`, `/links/{slug}/slots`, `/bookings`, `/bookings/{token}`, `/bookings/{token}/cancel`, `/bookings/{token}/reschedule` under `/api/v1`, plus the client's `/book/:slug` and `/manage/:token`. The only integer anywhere in a path is the `v1` API version, which identifies nothing. The `Location` header on a create names the 64-hex manage token.

Eight bookings, read back in insertion order:

```
01A0021D-11E7-72BF-…   01A0021D-637F-714F-…   01A0021D-ABF3-75F7-…   01A0021D-AC00-7310-…
01A0021D-AC0E-75F3-…   01A0021D-AC1B-7A20-…   01A0021D-AC29-73EB-…   01A0021D-AC39-77E4-…

SELECT COUNT(*) ... WHERE Id <= previous Id  ->  0
```

Ascending lexicographically, every version nibble a 7. The inserts went through HTTP one at a time and landed 13–16 ms apart at the tightest, so every identifier is in a distinct millisecond — which is the only thing v7 guarantees. `Guid.CreateVersion7()` fills the sub-millisecond bits with random data and has no counter, so ids minted inside a single millisecond come back in random order and a tight insert loop would fail this condition correctly.

### 8. Bundle budget — pass

69,107 bytes gzipped, budget 76,800. See above.

### 9. Allocation budget — pass

`AllocationTests`, 2 tests, 0 failed. 8,240 bytes for the seeded configuration. See above.

### 10. Idle memory — fail

159.2 MiB RSS against a target of under 100 Mi, on a platform whose empty-app floor is already 80.5 MiB. See above. Needs measuring on the deployment target before it is treated as a defect in Kairos.

### 11. The full suite — pass

16 passed, 0 skipped, 0 failed.
