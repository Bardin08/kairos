# Measurements

What Part A actually costs, and the evidence that its eleven acceptance conditions hold. Every number here was measured rather than estimated; where a measurement disagreed with an earlier one, the measurement is what is written down.

Measured 2026-08-14 on macOS 27 / arm64, .NET 10.0.101, framework-dependent Release publish. Reproducing this on the Raspberry Pi the app is meant to live on will move the memory figure and nothing else — see condition 10.

Conditions 2, 3, 4, 7, 9, 10 and 11 were measured against `99c874a`. Conditions 1, 5 and 6 were re-measured against `223079b`, which changed how the booking page derives dates. Condition 8 was measured again at `934e56a`, which moved the bundle without touching slot computation, zone rendering or the theme contract. The figures below are the latest in each case.

## How Part A was built

All eleven tasks are complete, and every change reached `main` through its own pull request. That is checkable rather than asserted: `main` holds 28 commits, of which the first is the repository's initial commit and the other 27 are each a squash merge carrying its pull request number. **27 pull requests merged, 27 commits, nothing pushed to `main` directly, nothing batched.** Two dependabot bumps (#2, #3) are open and are not task work.

## Headline

```
bundle          69.2 kB gzipped   (budget 76.8 kB)
allocations      8.2 kB per month of slots   (budget 65.5 kB)
idle RSS       159.2 MiB          (target under 100 Mi — MISSED, see below)
verify.sh       16 checks, 0 failed
```

## Bundle

`bash scripts/bundle-budget.sh` is the only place the budget number lives. It charges the budget for what the booking route loads on first paint — the entry chunk, every chunk it imports eagerly, and their CSS — read out of Vite's manifest; lazily-loaded chunks are weighed and printed alongside it and are not charged. The readings in this section were taken before the admin code split, when the script weighed everything in `wwwroot/assets/` at once and the two numbers were still one.

```
bundle-budget: 69152 bytes gzipped across 1 file(s), budget 76800
```

One file, because Vite emitted no CSS chunk: Kairos's stylesheet is the vendored `wwwroot/kairos.css`, which the API serves directly and which no build measures. 7,648 bytes of headroom. The React 19.2 shell alone was 61,641, so the two real pages, four components and three helpers cost roughly 7.5 kB gzipped between them.

Where it went, so a future jump has something to be argued against:

```
68,789   99c874a   the two pages as first shipped
69,107   223079b   +318   zonedOffset(), and the booker's form state hoisted into Book
69,152   934e56a   +45    focus gated on intent, a realm-independent abort guard,
                          and an unreadable organizer zone getting its own message
```

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

69,152 bytes gzipped, budget 76,800. See above.

### 9. Allocation budget — pass

`AllocationTests`, 2 tests, 0 failed. 8,240 bytes for the seeded configuration. See above.

### 10. Idle memory — fail

159.2 MiB RSS against a target of under 100 Mi, on a platform whose empty-app floor is already 80.5 MiB. See above. Needs measuring on the deployment target before it is treated as a defect in Kairos.

### 11. The full suite — pass

16 passed, 0 skipped, 0 failed.

---

# Part B

What Part B's eight acceptance conditions actually do when they are run against a real Google account, a real relay and a real receiver. Same rule as Part A: every line below is something that was observed, and where an observation contradicted an earlier claim the observation is what is written down.

Measured 2026-08-15 at `d019f1b` on macOS 27 / arm64, .NET 10.0.101, framework-dependent Release publish. Two instances were used. The **live** one on port 5199 runs against `backend/src/Kairos.Api/kairos.db` with the operator's Google account `vbardin810@gmail.com` connected (`connectedAt 2026-08-15T00:08:48Z`, `reconnectRequired false`) and the Gmail relay configured; conditions 1, 2, 5 and 7 are its. A **scratch** instance on port 5399 runs against a copy of that database with the credential row deleted, so nothing it does can reach the operator's calendar; conditions 3, 4, 6 and 8 are its, because each of them needs either a dead credential, a webhook receiver or a booking that has already ended.

## How Part B was built

`main` holds 40 commits: one initial commit and 39 squash merges, each carrying its pull request number. **Nothing was pushed to `main` directly.** Part A was #1–#30; Part B is **#31–#41 — eleven merges for nine tasks**, the two extra being fixes (#35, which made `ship.sh` prove a fetch actually moved `origin/main`, and #41, which bounded the `meeting.ended` sweep to meetings not yet announced). #2 and #3 are dependabot bumps and are still open.

## Headline

```
backend tests    136 passed, 0 failed     (Part A left it at 87)
frontend tests    32 passed, 0 failed
bundle            70,691 bytes gzipped    (budget 76,800)
verify.sh         16 passed, 0 skipped, 0 failed
conditions        6 pass, 2 partial, 0 fail
```

The bundle moved from Part A's 69,152 to 70,691 — **+1,539 bytes gzipped for the integrations page**, which is the only screen Part B adds. 6,109 bytes of headroom left.

## The eight conditions

### 1. An external event suppresses its slots and dims the day — pass

Two events were written into the connected calendar **through the Google Calendar API directly, not through Kairos**, so no `Booking` row existed for either and the only thing that could suppress a slot was the freebusy read. Google normalised both into `Europe/Kiev`, which is that calendar's own zone:

```
kairostaskten1   2026-09-02  10:00-12:00 Warsaw   stored 2026-09-02T11:00:00+03:00 .. 13:00+03:00
kairostaskten2   2026-09-03  08:30-17:30 Warsaw   stored 2026-09-03T09:30:00+03:00 .. 18:30+03:00
```

`GET /links/consulting/slots?duration=25` over both days, before and after:

```
2026-09-02   before  16: 07:00 07:30 08:00 08:30 09:00 09:30 10:00 10:30 11:00 … 14:30   (UTC)
2026-09-02   after   10: 07:00                                     10:30 11:00 … 14:30
2026-09-03   before  16
2026-09-03   after    0
```

Exactly the six starts 07:30–10:00Z went. The busy block is 08:00–10:00Z and the global margin is 15, but `consulting`'s own margin is 30 and `SlotGenerator` takes the larger, so the shadow is 07:30–10:30Z: 07:00 survives because a 25 ends at 07:25, before the shadow opens, and 10:30 survives because the interval is half-open. Nothing in the local database changed between the two reads.

The dimming, read out of a real browser at `Europe/Warsaw` on the September grid:

```
1 September   data-state=available     enabled    "Tuesday 1 September, 16 times available"    color rgb(23,26,32)
2 September   data-state=available     enabled    "Wednesday 2 September, 10 times available"  color rgb(23,26,32)
3 September   data-state=unavailable   disabled   "Thursday 3 September, no times available"   color rgb(155,161,176)
4 September   data-state=available     enabled    "Friday 4 September, 16 times available"     color rgb(23,26,32)
```

Both events were deleted afterwards and both days went back to 16.

### 2. The booking lands in the calendar at the right instant, with a Meet — pass

Booking `01a002c9-6690-75ad-b172-651b478067bf`, `2026-09-07T07:00:00Z`–`07:25:00Z`. The event Google returns when it is read back:

```
id            01a002c9669075adb172651b478067bf      the booking id as hex, so the write is idempotent
start         2026-09-07T10:00:00+03:00 (Europe/Kiev)     = 2026-09-07T07:00:00Z
end           2026-09-07T10:25:00+03:00 (Europe/Kiev)     = 2026-09-07T07:25:00Z
summary       Consulting call with Vlad Bardin
hangoutLink   https://meet.google.com/mnd-tvyf-wkw
conferenceData.createRequest.status.statusCode  success
```

and the row Kairos wrote beside it:

```
CalendarEntries   State 20 (Synced)   ExternalEventId 01a002c9669075adb172651b478067bf   LastError NULL
ConferenceLinks   ExternalId mnd-tvyf-wkw   JoinUrl https://meet.google.com/mnd-tvyf-wkw
```

The instant is the assertion, and three surfaces agree on it. The manage page rendered in `Europe/Warsaw` reads **`Monday 7 September, 09:00–09:25 · Times in Europe/Warsaw`**; the confirmation's subject reads `Monday 7 September 2026 at 09:00 (Europe/Warsaw)`; the calendar holds `07:00:00Z`. 09:00 CEST is 07:00Z is 10:00 EEST, one moment named three ways.

**One thing an operator should know rather than discover.** The account's Google Calendar is set to `Europe/Kiev` and Kairos's organizer zone is `Europe/Warsaw`, so the event the organizer sees in Google's own UI reads 10:00 for a slot the booking page offered at 09:00. That is not a Kairos defect — `GoogleCalendarAdapter` writes an offset-carrying instant and names no zone at all, which is why the instant survives the disagreement — but the two settings disagree by an hour and only one of them is Kairos's to fix.

### 3. A failed calendar write leaves the booking standing — pass

Reproduced independently of Task 8, on the scratch instance, without touching the live credential. A well-formed credential was planted: real AES-GCM under the real `Security__SecretKey`, holding a refresh token that has never existed, against a `client_id` that has never existed. Then one ordinary `POST /bookings`:

```
HTTP 201
Bookings         Status 10 (Confirmed)   1788850800..1788852300   intact
CalendarEntries  State 30 (Failed)       ExternalEventId NULL
                 LastError  Error:"invalid_client", Description:"The OAuth client was not found.", Uri:""
ConferenceLinks  0 rows
log              warn  Calendar sync failed for booking 01a002cc-…. The booking stands.
```

No 500, and the freebusy read on the same request logged its own warning and returned no opinion rather than failing the page — the two halves of the adapter failing differently, as documented.

**What "marked not synced" means today, precisely:** it is `CalendarSyncState.Failed` on a `CalendarEntry` row, and a warning in the log. Nothing in the API or in the UI surfaces per-booking sync state — `GET /integrations` reports whether the provider is connected, not whether a given meeting made it. An operator learns that a meeting never reached the calendar by reading the database or the log.

### 4. `.ics` `DTEND` and the reschedule `SEQUENCE` — partial

The .ics is right. Nothing delivers the second one.

Both dumps below come from `BookingMailer.Compose` — the production composition path, the one whose remarks say "exactly as it goes on the wire" — run over the real `Booking` row, written out to bytes and parsed back before anything was read off it. Booking `01a002c8-7804-7d51-a736-a5e604e79982`, 25 minutes, rescheduled through `POST /bookings/{token}/reschedule`:

```
                  before                          after
booking.Sequence  0                               1
StartUtc          1788505200                      1788512400
UID               01a002c8-7804-…-a5e604e79982    01a002c8-7804-…-a5e604e79982     unchanged
DTSTART           20260904T070000Z                20260904T090000Z
DTEND             20260904T072500Z                20260904T092500Z                 25 minutes both times
SEQUENCE          0                               1
METHOD            REQUEST                         REQUEST      (MIME method= agrees)
attachment        application/ics invite.ics      application/ics invite.ics
```

`DTEND − DTSTART` is 1500 seconds in both, the UID is byte-identical across the move, and `SEQUENCE` advanced by one. That is the condition as written, and it holds.

**Why this is partial.** `RescheduleAsync` takes neither `BookingMailer` nor `CalendarSyncService` — grep the file: `SendConfirmationAsync` and `SyncAsync` have exactly one call site each, both inside `CreateAsync`. So moving a meeting increments a `SEQUENCE` that is never sent to anybody and leaves the Google event at the old time. The invitee keeps an invitation for a slot that no longer exists, and the organizer's calendar keeps a meeting nobody is coming to. The .ics is correct; the delivery of it does not happen.

### 5. Confirmation in the inbox, with a working accept/decline — partial

**Gmail: confirmed by the operator, visually, in their own inbox.** Not by this run — that mailbox is not readable from here. What they reported seeing:

```
Subject      Confirmed: Consulting call, Monday 7 September 2026 at 09:00 (Europe/Warsaw)
Placement    inbox, not spam
Controls     Yes / No / Maybe / Propose a new time
Organizer    vbardin.homelab@gmail.com
Body         the manage link, http://localhost:5199/manage/<64 hex>
Attachment   one, "Scanned by Gmail"
```

The RSVP controls rendering is the load-bearing part: Gmail only draws them when the MIME `method=` parameter and the .ics `METHOD` agree, which is the one thing `BookingMailer` derives from a single constant rather than writing twice.

Gmail also printed **"On your Google Calendar — Conflict with Consulting call with Vlad Bardin at 9:00 AM – 9:25 AM"**, which is the calendar event from condition 2 and the .ics from condition 4 agreeing on the same instant, the same zone and the same 25 minutes, having reached Gmail by two entirely different code paths. It corroborates all three conditions better than any of them does alone.

**Outlook: not tested.** The operator has no Outlook or Hotmail address and asked for it to be skipped. This is the half more likely to fail: Microsoft's consumer filters score a `gmail.com` sender to an `outlook.com` recipient considerably harder than Gmail scores mail arriving from Google's own infrastructure, and the sending domain here is a free Gmail account rather than the `vbardin.com` the spec assumed. Nothing about the Gmail result predicts it.

**And the message that arrived was relayed by hand, because Kairos could not send it.** See the defect below; the bytes were Kairos's own, the SMTP client was not.

### 6. `meeting.ended` fires once and only once — pass

Scratch instance, webhook receiver on 127.0.0.1:5388, one booking hand-inserted with an end time ten minutes in the past.

```
00:13:22Z   booking 01A002C4-…-0000000000E6 inserted, EndUtc 2026-08-15T00:03:22Z, Status 10
00:14:13Z   poller queued it        one "Queued meeting.ended" line, one outbox row
00:14:23Z   dispatcher delivered it 61 s after the insert
00:17:17Z   six poller sweeps in the log, still one queued line, still one delivery
```

Three further sweeps ran after the announcement and produced nothing. The outbox row shows why: `Type 30, Attempts 0, DispatchedAt 1786752863`, one row, and the poller's candidate filter excludes any booking that already has a `MeetingEnded` message. End to end that is 51 s of noticing and 10 s of delivering, against a worst case of 70 (60 s poller + 10 s dispatcher).

The `booking.created` half is the same queue on the same tick and was measured alongside: queued 00:12:46Z, delivered 00:12:53Z, seven seconds, one dispatcher tick.

### 7. Neither fact lives on `Booking` — pass

Storage first, because it is the level that cannot be talked around:

```sql
CREATE TABLE "Bookings" (
    "Id", "LinkId", "StartUtc", "EndUtc", "Notes", "Status", "ManageTokenHash",
    "Sequence", "CreatedAt", "CancelledAt", "SeriesId", "RecurrenceRule", "RecurrenceId"
);
```

No event id, no join URL, no sync state, and no nullable column standing in for one. The entity matches — `Booking` has thirteen properties and one navigation, `Participants`, and no navigation to either new table. `BookingResponse`, which is what every booking endpoint answers with, names none of them either.

The two facts live where they belong, each under a unique index on `(BookingId, Provider)` so a provider can say each thing about a booking exactly once:

```
booking synced to a calendar   -> CalendarEntries  01A002C9-6DCE-…  State 20  ExternalEventId 01a002c9669075adb172651b478067bf
booking with a Meet link       -> ConferenceLinks  01A002C9-6F97-…  JoinUrl https://meet.google.com/mnd-tvyf-wkw
booking whose calendar refused -> CalendarEntries  01A002CC-E1E3-…  State 30  ExternalEventId NULL, and no ConferenceLink at all
```

The third row is the one that shows the split is load-bearing rather than decorative: a booking can have a calendar entry and no conference, and the absence of a `ConferenceLink` row is what "no join link" is made of — there is no state column on it to mean anything else.

### 8. The HMAC verifies — pass

Verified against an independent receiver written from the contract rather than from the code, checked first against the published vector — secret `a-shared-secret`, timestamp `1788000000`, body `{"type":"meeting.ended"}` → `sha256=2947a6ec…45fd0d` — and reproduced exactly before any Kairos delivery was trusted.

The `meeting.ended` delivery as it arrived:

```
Content-Type       application/json; charset=utf-8
X-Kairos-Timestamp 1786752863
X-Kairos-Signature sha256=8f5865dc1952867b9954eb617e31c14910c270c16805c8efa4c0e9d518e3fd4b
body               177 bytes
{"Event":"meeting.ended","BookingId":"01a002c4-0000-7000-8000-0000000000e6","Slug":"consulting","Start":"2026-08-14T23:38:22Z","End":"2026-08-15T00:03:22Z","DurationMinutes":25}
```

Recomputing `HMAC-SHA256(secret, "{timestamp}.{body}")` over the raw received bytes reproduced that signature exactly. Four negative controls, each of which must fail and each of which did:

```
one bit different in the secret          rejected
one byte appended to the body            rejected
timestamp advanced by one second         rejected
body signed without the timestamp        rejected
```

The last is the one that matters: it is what proves the timestamp is inside the signature rather than merely beside it, which is what stops a captured delivery being replayable forever. `booking.created` verified on the same terms, 179 bytes, `sha256=ab6c9755…c037653`.

## What Part B does not do yet

Four things, none of which any of the eight conditions asks for, and all of which someone will meet.

**`ICalendarWriter.CancelEventAsync` has no caller.** It is declared, implemented against Google, and faked in the tests; `grep` finds no production call site. Cancelling a booking removes it from Kairos and leaves the Google event where it was.

**A reschedule notifies nobody and moves nothing.** Covered under condition 4. Of the two, this is the worse one: cancelling at least leaves an event the organizer can recognise as stale, while a reschedule leaves two plausible times and no way to tell which is real.

**Confirmation email fails at the TLS handshake on this host — every time, not intermittently.** MailKit's `SmtpClient` defaults `CheckCertificateRevocation` to true and treats an *incomplete* OCSP lookup as fatal:

```
MailKit.Security.SslHandshakeException
  • The server certificate has the following errors:
    • An incomplete certificate revocation check occurred.
```

Across both live logs, **five bookings, five send attempts, five failures, zero successes.** The chain is fine — `smtp.gmail.com` presented a valid Google Trust Services WE2 certificate over TLS 1.3, and an independent client on the same machine at the same moment completed STARTTLS, authenticated with the same credential and delivered a 2,744-byte message. So this is not a network problem, not a credential problem and not interception; it is one library default meeting a platform that cannot complete an online revocation check. The message the operator found in their inbox is that hand-relayed one, carrying Kairos's own composed bytes. A fix is in flight as its own pull request.

**The sender is `vbardin.homelab@gmail.com`, not `vbardin.com`.** The spec assumed a relay with SPF and DKIM for `vbardin.com`; there is none, and a free Gmail account cannot send as a foreign domain with aligned DKIM. SPF and DKIM are Google's and are aligned, which is what the Gmail result rests on. Accepted by the operator. Related and also accepted: the relay credential is a Gmail app password shared with the identity stack and cannot be scoped send-only — it authenticates over IMAP too — which is documented in `homelab/docs/runbooks/credential-rotation.md` with the dedicated `vbardin.homelab@gmail.com` account as the mitigation.

---

# Part C

What Part C's conditions do when they are run against a real instance, in a real browser, at real viewports. Same rule as Parts A and B: every line below is something that was observed, and where an observation contradicted an earlier claim the observation is what is written down. Four of these conditions had never been run by anybody — they need a layout engine, and jsdom has none — so this is the first record in the project that answers them at all.

Measured 2026-08-15 at `244dffc` on macOS 27 / arm64, .NET 10.0.101, framework-dependent Release publish, 16 KiB pages. One instance on port 5300 with its own SQLite file, seeded through the admin API with two links — `consulting` (durations 25 and 50, margin 30, notice 120, cap 4, increment 30) and `mentorship` (single duration 30, margin 0, increment 15) — over a Mon–Fri 09:00–17:00 pattern with a 09:00–09:45 override on 25 September, organizer zone `Europe/Warsaw`. Browser conditions used headless Chromium 151 via Playwright 1.62.1 with an explicit `timezoneId`.

## How Part C was built

`main` holds 53 commits: one initial commit and 52 squash merges, each carrying its pull request number. **Nothing was pushed to `main` directly.** Part A was #1–#30, Part B #31–#42 with #43 as its follow-up fix. Part C is **#44, #45, #47, #48, #49, #50, #52, #53 and #54 — nine merges for six tasks**, the three extra being a bundle-budget scope fix, a test pinning the throw site a revoked grant actually uses, and a refactor moving two integration routes under the admin prefix. #46 and #51 landed in the same window and are Part B follow-ups rather than Part C work.

## Headline

```
booking route     71,471 bytes gzipped   (ceiling 76,800 — 5,329 free)
admin chunk        9,295 bytes gzipped   (lazy, not charged)
idle RSS           190.2 MiB             (target under 100 Mi — MISSED, and worse than Part A)
backend tests      183 passed, 0 failed  (Part B left it at 136)
frontend tests      68 passed, 0 failed  (Part B left it at 32)
verify.sh          16 passed, 0 skipped, 0 failed
conditions         10 pass, 1 partial, 4 fail, 1 unrun
```

The four failures are real and none of them is a surprise to the code: two are rules DESIGN.md contradicts elsewhere in its own text, one is a deliberate single exception with an ESLint disable and a written reason beside it, and one is a memory target that has been missed since Part A.

## The browser this record needed

Four of the design conditions and all three of the rules that decay are viewport or pixel checks. They were carried unrun through Parts A and B and through every Part C implementation task, and every agent that met them disclosed the gap rather than reasoning about pixels from a stylesheet. This record closes it.

**`playwright@1.62.1` is now a devDependency of `frontend/`, pinned exactly, and `scripts/measure-ui.mjs` is the only thing that uses it.** That script is not in `scripts/verify.sh` and should not be: it needs a seeded instance, an operator session and a browser download, none of which belong in a gate that runs on every commit. It is run by hand, and the command is in its header.

**The operator session was minted rather than obtained**, because there is no Pocket ID to talk to here. `scripts/measure-ui.mjs` is handed an `.AspNetCore.Cookies` value produced by protecting a `Cookies` authentication ticket with the application's own data-protection key ring and application discriminator. Everything downstream of that cookie is the application exactly as it ships — the same policy, the same handlers, the same database. What is not exercised is the OpenID Connect leg itself, which no test in this repository exercises either.

That the cookie could be minted at all is a finding in its own right, and it is condition-shaped: **the `admin` policy is `RequireAuthenticatedUser()` and nothing more.** The forged ticket carried `sub=part-c-operator`, a subject that exists in no identity provider anywhere, and it was admitted to every write endpoint on the workbench. Kairos has no `sub` allowlist and no claim check; the only thing standing between an arbitrary principal and the operator's data is whichever users Pocket ID is configured to issue tokens for. That may be the right place for the control, but it is a decision nobody has recorded, and until it is recorded the boundary is "whatever the IdP happens to be set to".

## The design system's seven conditions

The first of the seven — *booking completes with JavaScript disabled* — was withdrawn when the surface moved to React, and is replaced by *the booking route's bundle size is measured and recorded*. That measurement is under **Resources** below, with the rest of the weight.

### 2. 440×956 — pass, with one thing not exercised

The two panes stack, the primary action is alone, and it is at the bottom edge.

```
pane 1   x 17   y 61.0    406 x 204.5
pane 2   x 17   y 265.5   406 x 915.4      same x, so stacked rather than side by side

.btn-primary visible, no time chosen      0
.btn-primary visible, time chosen         1   "Continue"   y 900   height 44
.btn-primary visible, details step        1   "Confirm booking"   and .actionbar is gone

.actionbar   position fixed   top 887   bottom 956   viewport height 956
             padding-block-end: calc(var(--space-3) + env(safe-area-inset-bottom))
```

The bar's bottom edge and the viewport's bottom edge are the same pixel, and the desktop CTA is absent — the two are mutually exclusive at this width, as Part A found at 420px. One primary action in every state that has one, and none in the state that has nothing to commit yet.

**Not exercised: a non-zero safe-area inset.** `env(safe-area-inset-bottom)` resolved to `0px`, because headless Chromium has no notch to emulate and Playwright cannot set one. So what was measured is that the rule reserves the inset and that the bar sits flush at inset zero, not that it clears a home indicator on a real phone. Proving that needs a device or a simulator, and neither is a browser automation problem.

### 3. Duration 25 → 50 changes the times and the days — pass

Re-run as a regression against Task 3's first run, and it reproduces Part A's numbers exactly.

```
times   24 Sep @25: 09:00 … 16:30   (16 starts)
        24 Sep @50: 09:00 … 16:00   (15 starts)
days    open @25: 1,2,3,4,7,8,9,10,11,14,15,16,17,18,21,22,23,24,25,28,29,30
        open @50: 1,2,3,4,7,8,9,10,11,14,15,16,17,18,21,22,23,24,   28,29,30
        closed by the switch: 25 September
```

Both halves moved, and 25 September is the day that carries the 09:00–09:45 override: a 25 fits at 09:00, a 50 does not fit at all.

### 4. Tabular figures — fail

The booking surface is clean and the workbench is not.

```
booking page   52 numeric leaf elements, 52 with font-variant-numeric: tabular-nums, 0 without
manage page     2 numeric leaf elements,  2 with,  0 without
slot column     width 189px on Monday 17 August, 189px on Tuesday 18 August — no shift
admin           counts and clock fragments outside the rule, listed below
```

One rule carries the whole condition — `time, .numeric, td.numeric, .slot, .day, .stat-value` — and it is a list of selectors rather than a property of numbers, so a figure is tabular only where somebody remembered to say so. Where it was forgotten, measured in the browser:

```
/admin/availability   p.fine       "1 single date set"                        a count, not tabular
/admin/settings       p.fine       "Kept clear … 2 links set their own …"     a count, not tabular
/admin/settings       p.fine       "30 gives you :00 and :30. 2 links …"      a duration, two clock
                                                                             fragments and a count
```

Three more were found by reading rather than by running, and are recorded here because the run could not reach them:

- **`Manage.tsx:426`** renders the reschedule CTA as `className="btn btn-primary"` around `Move to {chosen.long}` — a full date and time span that re-renders on every slot click, in a control that no tabular rule reaches. The identical control in `Book.tsx:526` is `className="btn numeric"`. This is precisely the jitter case the rule's own comment exists to prevent, and it is the sharpest one.
- **`Availability.tsx`** has five `<input type="time">` / `type="date"` on a bare `.input`, at lines 265, 272, 323, 344 and 351, sitting in the same card as `.numeric` spans that are tabular. They were not visible in the page's default state, so the browser pass reports no lacking input; the source says otherwise.
- **`<option>` text is reachable by no rule at all** — `Settings.tsx:269` prints a UTC offset, `Meetings.tsx:261` prints "Last 30 days", and both are visible in the closed control.

Two hygiene notes from the same read: `.stat-value` in the rule is dead — no `.stat` markup exists anywhere — and `td.numeric` is redundant with `.numeric` beside it.

### 5. Theme forced against the system preference, and contrast on the accent — pass

First-frame values captured in a `requestAnimationFrame` registered before any page script, the same method Part A used, and now with a control that exists: **Part C ships the theme control**, in Settings, as a three-state select.

```
nothing stored, OS light   first frame: attr=null   --surface-page #FFFFFF   --accent #4E56E3
nothing stored, OS dark    first frame: attr=null   --surface-page #0E1014   --accent #737DEE
stored dark,   OS light    first frame: attr=dark   --surface-page #0E1014   --accent #737DEE
stored light,  OS dark     first frame: attr=light  --surface-page #FFFFFF   --accent #4E56E3
```

Both directions of the override hold, and neither flashes: the first frame already carries the settled value, including where the stored choice contradicts the OS.

Every element painted with the accent that carries text, in both themes, with its measured ratio:

```
light   .day[aria-pressed]   #FFFFFF on #4E56E3   5.58:1
        .slot[aria-pressed]  #FFFFFF on #4E56E3   5.58:1
        .btn-primary         #FFFFFF on #4E56E3   5.58:1
dark    .day[aria-pressed]   #0B0D2B on #737DEE   5.33:1
        .slot[aria-pressed]  #0B0D2B on #737DEE   5.33:1
        .btn-primary         #0B0D2B on #737DEE   5.33:1
```

Nothing falls below 4.5:1 in either theme, and the prohibited pair is prohibited in fact rather than by intent: **white on the dark-mode accent measures 3.56:1**, which is what `--text-on-accent` going to `#0B0D2B` in dark is for. The stylesheet's own comment says "~3.5:1"; the measurement agrees with it.

### 6. Keyboard alone reaches every control on both surfaces — partial

Tab was pressed until the focus returned to something already seen, and what it reached was compared against every visible focusable element on the page.

```
/book/:slug, calendar step   30 controls, 30 reached
/book/:slug, details step     5 controls,  5 reached
/manage/:token                covered by the cancel run below
/admin/links                  7 controls,  7 reached
/admin/meetings               6 controls,  6 reached
/admin/availability           8 controls,  8 reached
/admin/settings              13 controls, 13 reached
```

Nothing is unreachable, and no control was focused with neither an outline nor a shadow. Two things stop this being a clean pass.

**The ring on a form control is not the ring.** `:focus-visible` gives everything else `outline: 2px solid var(--focus-ring)` at `outline-offset: 2px`. `.input:focus, .textarea:focus, .select:focus` sets `outline: none` and replaces it with an accent border plus a 3px `--accent-subtle` halo — and that rule is in the `components` layer while the base ring is in `base`, so it wins regardless of specificity, and it matches `:focus` rather than `:focus-visible`. Measured on a focused settings field:

```
outline      rgb(23, 26, 32) none 3px      style is `none`, so nothing is drawn
border-color rgb(78, 86, 227)              --accent, changed from rgb(225, 227, 234)
box-shadow   rgb(238, 240, 254) 0 0 0 3px  --accent-subtle
```

The border change is a clear signal. The halo is not: `#EEF0FE` on `#FFFFFF` is **1.13:1**, and `#1A1D3D` on `#171A20` is **1.07:1**. So a focused field is marked by one channel, not two, and the design system's "the ring is never removed, only inset where a container would clip it" is not true of the controls this sentence is most about.

**The slot grid clips the ring by one pixel.** `.slots` above 861px is `overflow-y: auto` with `padding: 3px; margin: -3px`, added — its comment says so — to make room for the focus ring. The ring needs 4px: 2px of offset plus 2px of width. Measured on the first slot with focus on it, the slack between the button's box and the scroll container's is `top 3, left 3`, and on the second column `right 3`. The outermost pixel of the ring is clipped on the first and last rows and on both outer columns. `padding: 4px; margin: -4px` closes it.

### 7. No inline `style` attribute in the built markup — fail

```
/book/consulting     16 elements with a style attribute — all button.slot, all `--i: N`
/manage/:token        0
/admin/links          0
/admin/meetings       0
```

This is the exception the project already knows about: the stagger's per-item delay is `animation-delay: calc(var(--i) * …)`, which needs a per-item custom property, and `SlotList.tsx:101` carries a per-line `eslint-disable-next-line react/forbid-dom-props` with its reason written beside it. The condition as written does not hold — sixteen inline `style` attributes are sixteen inline `style` attributes — and the honest reading is that the condition is stated more absolutely than the design it is checking. Every other surface is clean, and the rule that would catch a second exception is armed: `--max-warnings 0` turns an unused disable directive into a build failure.

## The three rules that decay

### Two Jobs — fail

The accent has more than two jobs, and it has different extra jobs at different widths. Measured by walking every element that is actually displayed and comparing its computed `background-color`, `color` and border colours against the resolved accent tokens.

```
1280px, rail    background  span.wordmark-glyph                --accent        the logo mark
                color       span.avatar.avatar-sm "VB"          --accent-text   the operator's initials
                background  button.btn-primary                  --accent        the commit action   OK
                the rail's current item: background rgb(237,238,242) = --surface-active,
                font-weight 600 against a sibling's 500 — no accent at all

 899px, tabbar   background  button.btn-primary                  --accent        the commit action   OK
                color       a[aria-current="page"] and its svg   --accent-text   the current tab
                ::before    height 2px, background rgb(78,86,227) = --accent     the edge bar
                the rail, and with it the wordmark and avatar, is display:none here
```

So the desktop half of the shell is compliant on navigation and the phone half is not, and both halves spend the accent on a logo mark that marks nothing. The tab bar's use is not an oversight either: `AdminShell.tsx:136-139` describes it deliberately, and DESIGN.md's own Navigation section instructs it — *"Phone: a bottom tab bar, the same three destinations, accent used only for the current one."* **DESIGN.md contradicts its own Two Jobs Rule**, and the rule is the thing that lost. Two further accent uses sit outside the two jobs and were not on this screen: `.pattern-day[data-on="true"]` on an availability card, and `a { color: var(--accent-text) }` on every anchor — load-bearing enough that `Book.tsx:400` and `Manage.tsx:485` both use a `<button>` instead of an anchor to escape it.

The clean fix for the shell is the one the rail already demonstrates: an inset fill and a heavier weight mark the current destination without the accent, and the tab bar's 2px bar is a shape rather than a hue and can keep its job in a neutral. Nothing here is a defect in the implementation; it is a rule and a spec disagreeing, and the disagreement has to be settled in one of them.

### Second Channel — pass

Monochrome screenshots at 1280px and 899px, of Links, Meetings and the shell — two screenshots and not one, because the rail and the tab bar carry different second channels and the tab bar's is the one that was caught failing during the design build.

```
Links     Live     filled dot   ●        Archived  hollow dot  ○   same size, same position
Meetings  Confirmed filled dot  ●        Cancelled hollow dot  ○
          Not synced hollow dot ○        plus the provider's own sentence underneath
rail      current: inset fill + weight 600 against 500 — both survive grayscale
tabbar    current: 2px edge bar above the tab — survives grayscale, and it is the channel
          that carries the state once the hue is gone
```

Every state is still readable with the hue removed. A link was archived before the screenshots were taken so that both sides of the live/paused pair appeared in the same frame; a screenshot in which every row is live proves nothing. The confirmed/cancelled pair came free, because the fourth phase condition below leaves cancelled meetings behind it.

### Inversion — pass

Dark is not the light palette flipped, and three independent tests say so.

```
                        light            255 - light would be       dark actually is
--surface-page          #FFFFFF          #000000                    #0E1014
--surface-sunken        #F6F7F9          #090806  (warm)            #0A0C0F  (cool)
--text-primary          #171A20          #E8E5DF  (warm)            #EEF0F4  (cool)
--accent                #4E56E3          #B1A91C  (olive)           #737DEE  (indigo)
```

Inverting a cool neutral produces a warm one and inverting indigo produces yellow; neither happens. Mirroring the ramp index instead matches 3 of 14 neutral tokens and 0 of 7 accent tokens. And three things no reversal can produce are present: `--text-tertiary` is `--gray-500` in **both** themes and `--focus-ring` is `--indigo-400` in both; the elevation structure changes, because light gives page and raised the same `#FFFFFF` and separates them by shadow while dark separates them by value (`#0E1014` against `#171A20`) and adds a sunken step *below* the page that has no counterpart at the top of the light ramp; and nine dark values are hand-chosen literals with no ramp member behind them. The status colours settle it by construction — the primitives carry `green-500`/`green-100` for light and `green-400`/`green-950` for dark, so the palette was authored twice on purpose.

## The phase's four conditions

### 1. Cross-timezone across the DST boundary — pass

The organizer zone was set **through the Settings screen in a browser**, not through the API: moved to `Europe/Lisbon`, saved, read back as `Europe/Lisbon`, moved back to `Europe/Warsaw`, saved, read back as `Europe/Warsaw`, with the screen answering `Saved.` both times. Then a booking with the invitee in `America/New_York` on Monday 26 October 2026 — the Monday after Poland falls back and the week before New York does, so the usual six-hour gap is five.

```
19 Oct (Warsaw still CEST)  first slots, rendered in New York:  03:00  03:30  04:00
26 Oct (Warsaw now CET)     first slots, rendered in New York:  04:00  04:30  05:00
```

Booked end to end through the page. The invitee's summary read **`Monday 26 October, 04:00–04:25`** and **`25 minutes · America/New_York · Consulting call`**. The same meeting in the organizer's own calendar, which is what Part C adds and Part A could not check:

```
/admin/meetings, "Times are the organizer's, in Europe/Warsaw."

  26  OCT 2026   09:00 – 09:25   25 min   DST Traveller
                                          dst@example.com
                                          04:00 their time · America/New_York
                                          Consulting call   Confirmed
```

One instant, named 09:00 by the organizer and 04:00 by the invitee, on a day when the offset between them is not the one it was a week earlier. The month grid also opened on `August 2026` with the invitee in New York — the organizer's current month, as Part A found.

### 2. Cross-link margin — pass, and this time 11:15 means something

`consulting` (margin 30) booked 10:00–10:50 Warsaw on 7 September, so the shadow is 09:30–11:20. `mentorship`, margin 0, was given a **15-minute start increment** for this run precisely because Part A's record notes that 11:15 proved nothing at a 30-minute increment: it was never a candidate. At 15 it is.

```
mentorship @30, 7 September, after the booking (Warsaw wall clock):
  09:00                                    11:30  11:45  12:00 … 16:30

  11:00  absent      a 30 starting there runs into the shadow
  11:15  absent      a real candidate at this increment, and genuinely blocked
  11:30  present     the first start clear of 11:20
  09:00  present     a 30 there ends exactly at 09:30, where the shadow opens — half-open
  09:30  absent
```

11:00 and 11:15 blocked, 11:30 permitted, on a link whose own margin is zero. The margin pads the busy interval and not the candidate, and it is the larger of the two links' margins that applies.

### 3. Two concurrent requests for one slot — pass

```
thread 0: HTTP 201  01a00349-108d-7340-87aa-a4062cfb68ed
thread 1: HTTP 409  {"code":"slot_taken","title":"That time was taken while this booking was being made."}
SELECT COUNT(*) FROM Bookings WHERE StartUtc = 1788850800 AND Status = 10  ->  1
```

Both requests were issued from one `Promise.all` against the same start instant. One booking, one rejection, one row.

### 4. Cancelling frees the slot and its margin shadow on every link — pass

A `consulting` booking of 10:00–10:50 Warsaw on 9 September, cancelled through the manage page in a browser — "Cancel meeting", then "Yes, cancel it" — leaving the page reading *Cancelled* and *This meeting was cancelled and the time is back on the organizer's calendar.* All three reads of that day, before and after:

```
mentorship @30    22 slots  ->  31 slots     restored: 07:15 07:30 07:45 08:00 08:15
                                                       08:30 08:45 09:00 09:15  (UTC)
consulting @25    12 slots  ->  16 slots     restored: 07:30 08:00 08:30 09:00
consulting @50    10 slots  ->  15 slots
```

The slot itself and the whole of its 30-minute shadow came back, on the link that made the booking and on the link that only ever felt the shadow.

## Resources

### Bundle — pass

`bash scripts/bundle-budget.sh` at `244dffc`, after `npm run build`:

```
bundle-budget: booking route 71471 bytes gzipped across 1 file(s), budget 76800
bundle-budget: lazy chunks 9295 bytes gzipped across 1 file(s), not charged to the budget
```

**5,329 bytes of headroom, 93.1% of the ceiling consumed.** The ceiling is 76,800 and is documented with per-React-version measurements in the script's own comment block; the 61,440 that preceded it was superseded during Part A when the React 19.2 shell alone measured 61,641 gzipped and did not fit its own budget.

The comparison that matters is what Part C cost the invitee. Part B's last reading was 70,691 for a build with no lazy chunk, so it was everything. Part C added an entire operator workbench — four pages, an editor, a record table, a shell — and the invitee's route moved by **780 bytes**, because all of it is behind `lazy(() => import('./routes/admin/AdminShell'))` and lands in the 9,295-byte chunk instead. Two admin-only calls do stay in the entry chunk on purpose: `/integrations` is a fixed top-level route the OAuth callback redirects a browser to, and `App.tsx` imports that page eagerly.

What can still move this number is shared code, since a helper pulled into both surfaces is charged to the invitee. Whether 76,800 is the right ceiling remains open and untouched.

### Idle memory — fail, and further from the target than Part A

Measured on a freshly started Release process with its own SQLite file, after loading both booking pages **and all four admin pages** in a real browser and idling 30 seconds. Part A measured this before the admin surface existed, so this is the number that counts.

```
                              Part C          Part A         change
ps rss                        190.2 MiB       159.2 MiB      +31.0 MiB
macOS physical footprint      144 MB          119.8 MB       +24.2 MB
  of which dirty, untagged    116 MB           95.7 MB
  of which malloc              22 MB           18.2 MB
```

RSS was sampled four times a second while a booking page and its month of slots were fetched, and it never moved off the idle figure: the highest reading of the whole run is the 190.2 MiB above. This process does not spike under load; it starts heavy and stays there.

**The conclusion has not changed and the gap has widened.** Part A recorded this condition as a fail at 159.2 MiB against a target of under 100 Mi; Part C is 190.2 MiB against the same target.

The floor underneath it was re-measured on the same machine on the same day rather than quoted from Part A, because a floor from a different runtime build would make the comparison meaningless. An empty `WebApplication.CreateSlimBuilder` app with one `MapGet`, published the same way:

```
empty app, idle   84.7 MiB rss / 57 MB footprint     (Part A measured 80.5 / 52.5)
```

So the floor drifted up by 4.2 MiB and Kairos's own share went from about 79 MiB to **105.5 MiB rss / 87 MB footprint**. Roughly 26 MiB of that is Kairos code and data rather than platform. It is not attributable to Part C alone: Part B added two hosted services that tick forever, a Google client factory and an SMTP path, and this instance has bookings in it where Part A's had fewer. What can be said precisely is that no part of this is the frontend — the workbench is 9 kB of JavaScript that never enters this process — and that the target was written for a Raspberry Pi on linux-arm64 with 4 KiB pages, which is a different runtime baseline entirely and where this has still never been measured.

### The pod's limits — the image exists, and the limit was run

This section previously read "there is no pod", and said the smallest missing piece was the image. That is no longer true: the repository now carries a `Dockerfile`, and the readings below are the first this project has taken inside a cgroup rather than beside one. There is still no Kubernetes manifest and no Helm chart.

Measured 2026-08-23 on macOS 27 / arm64, Docker 28.5.1, the image built from this tree, run as `--memory 192m --memory-swap 192m` — the phase's `limits 192Mi` expressed as the cgroup a kubelet would create. `memory.current` and `memory.peak` were read out of `/sys/fs/cgroup/docker/<id>` in the Docker VM, so these are the same counters `kubectl top pod` reports rather than a host process's RSS.

```
memory.max                        201,326,592     (192 MiB, the cap)
idle       memory.current          79,691,776     76.0 MiB
           memory.peak             82,604,032     78.8 MiB
after 600 requests
           memory.current          87,638,016     83.6 MiB
           memory.peak             90,071,040     85.9 MiB   <- high-water
```

**It runs, and it runs at 45% of the limit.** `/healthz` and `/readyz` both answered 200 throughout, nothing was killed, and the high-water mark is 106 MiB clear of the cap.

That is less than half the 190.2 MiB measured on the host above, and the difference is not an improvement to the application — no code changed between the two readings. Two things account for it, and both are the reason a host reading was never going to answer this question: the host is macOS/arm64 with 16 KiB pages while the container is Linux with 4 KiB, and a .NET process that finds a cgroup limit sizes its heap against that limit rather than against the machine's RAM. The Part A and Part C figures are not wrong; they were measuring a different thing.

**Scope, so this is not read as more than it is.** The 600 requests were the single page, the vendored stylesheet, both probes and an admin route answering 401 — not the booking page with a month of slots that the section above loads, because the database in a fresh container has no links in it. So this is an idle-plus-probe-traffic figure and the allocation behaviour above it still stands unchallenged. What it settles is the condition this section was written to record as unrunnable: the limit was applied, and the process lived inside it.

### The image

```
uncompressed   151,545,100 bytes   151.5 MB / 144.5 MiB   <- what lands on disk
compressed      59,366,980 bytes    59.4 MB /  56.6 MiB   <- what is pulled
```

**Name the convention or the number means nothing.** `docker image inspect --format '{{.Size}}'` does not answer the same question on every machine: on a daemon using the containerd image store — this one — it reports the sum of the compressed blobs, and on a classic dockerd it reports the sum of the uncompressed layer diffs. The two differ by a factor of 2.5 here. The uncompressed figure is the honest one for a target about disk on a Raspberry Pi; the compressed figure is the honest one for a target about pull time. Both are above, and the uncompressed one was cross-checked by summing `docker history` and by summing the exported rootfs.

Against the project's stated target of under 150 MB, the uncompressed figure is **1.5 MB over if that target is decimal MB, and 5.5 MiB under if it is MiB**. The target does not say which. Recorded rather than argued.

The number moved a long way to get there, and by removing dead weight rather than by tuning:

```
183.1 MB   the first working image
151.5 MB   -31.6 MB   publish pinned to one runtime identifier
```

An architecture-agnostic `dotnet publish` copies `runtimes/` for every platform any dependency was built against — 23 of them, 34.2 MB, of which a linux-arm64 image can execute exactly one. SQLitePCLRaw alone shipped `libe_sqlite3` for Windows, macOS, Mac Catalyst, browser-wasm, s390x, ppc64le, riscv64 and every musl variant. Publishing with `-r linux-arm64 --no-self-contained` leaves the single native library in `/app` and the tree gone.

What remains is not Kairos. The 89.2 MB layer is the .NET shared framework inside the chiselled base image, and the application's own layer is 19.1 MB of which BouncyCastle — a MailKit dependency — is 4.9 MB. Getting materially below this means self-contained publishing with trimming, which is a different decision with its own risks, and it has not been taken.

## The register of what is not covered

Everything below was carried into this task as unproved. Each line says what was done about it.

**Run here, and now proved:**

- **Every untested validation code on the patterns and meetings slices.** All of them fire, over HTTP, against the running application: `override_incomplete` 400, `pattern_name_taken` 409, `window_inverted` 400, `date_invalid` 400, `range_inverted` 400, `time_zone_unknown` 400, and the `[Range]` bounds — `IsoDayOfWeek must be between 1 and 7`, `StartMinute`/`EndMinute must be between 0 and 1439`, `StartIncrementMinutes must be between 1 and …` — each as a DataAnnotations 400.
- **"Dimmed rather than absent", end to end for the first time.** A real all-day override was written to 4 November 2026 through the admin API; the public slots read for that day returned `[]` and the day beside it returned 16. In the browser, on the November grid: 30 day buttons for a 30-day month, and

  ```
  4 Nov   data-state=unavailable   disabled   "Wednesday 4 November, no times available"
          colour rgb(155,161,176)   no availability dot
  5 Nov   data-state=available     enabled    "Thursday 5 November, 16 times available"
          colour rgb(23,26,32)      4px accent dot
  ```

  A real blocked override reaching a real dimmed cell, with the reason in the accessible name. No stubbed fetch anywhere in it.
- **The admin boundary from outside.** With no cookie, `/api/v1/admin/links`, `/meetings`, `/patterns`, `/settings` and `/integrations` all answer **401**. An unmatched `/api/` path answers **404** with no content type rather than 200 with HTML. `/admin/links` still answers 200 `text/html`, which is the single page and is correct.
- **`CalendarSyncState.Pending` reads as not-synced — and reads wrong.** A `Pending` row was inserted for a confirmed booking. The API answers `calendarSynced: false`, `calendarProvider: "google"`, `calendarError: null`, and the workbench renders **"Not synced — No calendar was connected when this was booked, so nothing was written."** For a `Pending` row that sentence is false: a calendar *is* connected and the write has not finished. `whyNotSynced` branches on `calendarError === null` alone, so it cannot tell "never attempted" from "in flight". Small, and worth a third branch.
- **Data protection keys are not persisted.** There is no `AddDataProtection` or `PersistKeysTo` call anywhere in `backend/src`, so the key ring is the framework default — `$HOME/.aspnet/DataProtection-Keys` — which in a container with no volume dies with the pod, and the operator logs in again after every restart. Accepted for one operator; now written down. No longer a deduction: the image logs it on every start — `Storing keys in a directory '/home/app/.aspnet/DataProtection-Keys' that may not be persisted outside of the container`, on the container's own writable layer and not on the `/var/lib/kairos` volume beside it. Pointing the key ring at that volume is one call and has not been made.

**Found while running, not previously recorded:**

- **`pattern_name_required` is unreachable.** `SavePatternRequest.Name` carries `[Required]`, which rejects null, empty and whitespace-only strings; `Named()` then trims and throws `pattern_name_required` when the result is empty. Both use the same predicate, so nothing that would reach the throw survives the attribute. `{"name":"   "}` and `{"name":"\t\n "}` both answer 400 `The Name field is required.` on POST **and** on PATCH, never the domain code. It is dead code with a documented constant and a doc comment.
- **A zero-width space is a valid pattern name.** `{"name":"​"}` was accepted and created a pattern, because `char.IsWhiteSpace('​')` is false and `Trim()` leaves it. The workbench now lists a pattern whose name draws nothing.
- **`.record-day` overflows its own line box.** 20px type at `line-height: 1.1` gives a 22px line box around a 23px ink box, so the digits sit one pixel proud at the top. Nothing clips them — every ancestor up to `.table-wrap` is `overflow: visible` — and a 4× screenshot with the box outlined shows the glyphs complete. It is one pixel of slack away from being clipped by anything that ever gains an overflow.
- **The `860` / `861` boundary has a gap.** `max-width: 860px` and `min-width: 861px` leave a fractional viewport in between matching neither, reachable by browser zoom. At 860.5px the panes stay two columns while the calendar goes to one, and the sticky action bar stays hidden. Degraded rather than broken — the desktop CTA is still there — but it is a hole.
- **The admin surface under-reserves for its own tab bar.** `.admin-main` reserves 96px at the bottom; the tab bar measures 1 + 44 + 1 + 56 = 102px plus `env(safe-area-inset-bottom)`. The booking page reserves 92px for an action bar that measures about 59px plus the inset, so the asymmetry runs the wrong way. On a phone with a home indicator the last block of every admin page sits under the tab bar.
- **No `safe-area-inset-left` / `-right` anywhere.** Both fixed bars use `inset-inline: 0` and `.page` uses a fixed inline padding, so in landscape on a notched device their content runs under the notch.

**Still unproved, with what proving it would take:**

- **A live Google grant revoked against a real account.** The throw site is pinned by a test (#52) and condition 3 of Part B planted a dead credential, but nobody has revoked a real grant and confirmed the row reads not-synced *with the provider's own words* rather than with the "no calendar was connected" sentence. Needs the operator's Google account, a revocation in their security settings, and one booking afterwards. This is a ten-minute manual check and it is the last one standing between "handled" and "observed".
- **Two `CalendarEntry` rows for one booking.** The read orders by `UpdatedAt` descending and takes the first. There is one provider, so the tie-break has never run. Needs a second provider, or a seeded second row with a deliberate tie.
- **`UniquelyNamedAsync`'s Add branch, on the patterns slice.** It is wired to three call sites in `PatternsAdminEndpoints` — pattern create, pattern rename and the override upsert — and the Add branch is only reachable when two writers both observe `replaced == 0`. The happy path is tested and the race is not. Two parallel writes dropped into a test are flaky rather than a test, so proving it needs a barrier-released pair like the one the third phase condition above uses for bookings.
- **A third `BookingStatus`.** `Meetings.tsx` renders `status === 'confirmed' ? 'Confirmed' : 'Cancelled'`. The enum has two values today and is numbered with gaps specifically so a third can be inserted; the day one is, this prints "Cancelled" for it, silently. A `switch` with an exhaustive default is the fix and it costs nothing now.
- **The OpenID Connect leg.** No test and no measurement in this repository has ever talked to a Pocket ID. The cookie half of the gate is exercised end to end and the challenge half is not.
- **`appsettings.Development.json` commits `"ClientSecret": "development-placeholder"`.** Not a real secret, and it keeps `dotnet run` working now that startup fails fast — but it is the shape into which a real secret gets pasted by accident, and nothing in the file says not to.

---

# The availability editor

What the two-pane editor at `/admin/availability/{patternId}` cost, and what it made cheap. Same rule as Parts A, B and C: every line below is something that was observed. The difference is the second instance — the "before" column here was not read off the old source, it was driven in a browser against the page this work replaced.

Measured 2026-08-16 at `3ee73e6` on macOS 27 / arm64, .NET 10.0.101, framework-dependent Release publish. Two instances, each with its own SQLite file and each signed into through the real OpenID Connect leg against the mock provider in `compose.dev.yaml` — no minted cookie this time, because `docs/running-locally.md` now makes the real one obtainable. Port **5301** serves the editor as it stands. Port **5302** serves the same published backend behind a `wwwroot` built from `25adcca^`, which is the 635-line page with the inline editor inside every card. Browser conditions used headless Chromium via Playwright 1.62.1 with `timezoneId: Europe/Warsaw`.

## How it was built

`main` holds 67 commits: one initial commit and 66 squash merges, each carrying its pull request number. **Nothing was pushed to `main` directly.** This work is **#61, #62, #63, #64, #65, #67 and #68 — seven merges for seven tasks**, one apiece, no fixes needed. #66 was closed unmerged as a duplicate of #69, and #69 and the still-open #70 are documentation rather than this work.

## Headline

```
block a date            1 click,       1 PUT      was 1 date typed + 1 click, 1 PUT, no calendar
shift a five-day week   2 presses,     1 PUT      was 17 interactions, 10 PUTs
close a day             1 click,       0 requests was 1 click and an immediate PUT
open a day              1 click,       0 requests was 1 select + 1 click and an immediate PUT
what a link offers      1 select                 was nothing at all on this screen

booking route     71,879 bytes gzipped   (budget 76,800 — 4,921 free)
admin chunk       11,155 bytes gzipped   (lazy, not charged)
backend tests        190 passed, 0 failed  (Part C left it at 183)
frontend tests        97 passed, 0 failed  (Part C left it at 68)
verify.sh             16 passed, 0 skipped, 0 failed
the ten checks        10 run, 10 pass, 0 fail, 0 unrun
```

Ten passes is an unusual line to write, so the two things that did not go well are said here rather than buried: **the week pane's only primary action is below the fold at 1280×800**, and **the day square is 39×40 against a 44px Tap Floor**. Neither is what any of the ten checks asks, and both are in the register at the end.

## The fixture

Seeded through the operator's own HTTP surface with the session cookie on it, so nothing was written behind the API's back. Organizer zone `Europe/Warsaw`, margin 0, minimum notice 0, daily cap 50, start increment 30 — the "no unusual limits" check 2 asks for, chosen so that nothing but the pattern can close a day. One pattern, `Weekdays 09–17`, open Monday to Friday 09:00–17:00. One active link, `consulting` / "Consulting call", single duration 25. A second pattern, `Weekend mornings`, with no rules and no links, so the list's "Not used by any link yet" line and the preview's no-picker branch both have a subject. The instance on 5302 was seeded identically by the same script.

`scripts/measure-availability.mjs` is what drives checks 1 through 8, and it is a sibling of `scripts/measure-ui.mjs` in every respect that matters: run by hand, never in `scripts/verify.sh`, and the command is in its header.

## The ten checks

### 1. A blocked date reaches the anonymous read — pass

One click on the square for 1 September 2026, in a signed-in browser, and then a slots read from Node with **no cookie on it at all** — a separate fetch, because a request made from a signed-in context proves nothing about what an invitee can see.

```
click                 PUT /api/v1/admin/patterns/{id}/overrides/2026-09-01
square, before        "Tuesday 1 September, open"
square, after         "Tuesday 1 September, blocked"

GET /api/v1/links/consulting/slots?from=2026-09-01&to=2026-09-30&duration=25, no cookie
  1 September  16 -> 0
  2 September  16 -> 16
  the month   352 -> 336
```

Sixteen starts left the month and sixteen is exactly one day of them, so nothing else moved. End to end from a square to an invitee's calendar, and the only write in between is the one the click made.

### 2. `patternDays` and the real slots agree — pass

The check holding the design together: the month grid computes open, closed and blocked in the browser, the slots endpoint computes bookable starts in the engine, and the two must not drift.

Run over **September 2026 — a whole month in the future**, deliberately. `patternDays.ts` documents that it knows nothing about margin, minimum notice, daily cap or start increment; a month with today in it would therefore disagree about this morning for a reason that is not drift, and the disagreement would drown the one that would be.

```
30 squares for a 30-day month
22  data-day-state="open"          22  days with at least one slot
 0  open with no slots              0  slots on a day not open
 8  data-day-state="closed"
16  slots on every one of the 22
```

The month-wide comparison cannot reach the case most likely to break, so it was run on its own: **an override that opens a day the week never opens.**

```
Sunday 6 September, before       square "closed"    anonymous slots 0
09:00–12:00 set on that date
Sunday 6 September, after        square "open"      anonymous slots 6
                                 07:00:00Z … 09:30:00Z
```

Six starts is what a 25-minute meeting at a 30-minute increment fits into three hours, the last one at 11:30 Warsaw ending at 11:55. The square and the engine reached the same answer from opposite directions, and neither was told about the other.

**What this is not.** One month, one link, one set of limits — a snapshot, not a property test. And in the current month the two do read differently, exactly as designed:

```
August 2026, today the 17th
19  data-day-state="open"
 9  of those also data-state="available"
10  open with no times: 3, 4, 5, 6, 7, 10, 11, 12, 13, 14 August
```

Ten open squares carrying no times, and they are precisely the ten weekdays already past. The day state is the pattern's answer and the count is the link's, and the grid draws both because they are different questions.

### 3. Blocking a date is one click — pass

From `/admin/availability/{patternId}` freshly loaded, on the month it opens on, with no arrow pressed:

```
clicks from page load   1
field entries           0
requests                1  (PUT .../overrides/2026-08-28)
click to repaint       46 ms
square                 "Friday 28 August, open" -> "Friday 28 August, blocked"
```

The 46 ms has the round trip inside it: the square only repaints from the pattern the write answers with, so nothing was drawn before the server agreed. A date in another month costs one arrow press per month of travel, and nothing else.

### 4. Shifting a five-day week is one press and one request — pass

Monday's two fields moved 09:00 → 10:00 and 17:00 → 18:00, then *Use these hours on every open day*, then *Save weekly hours*.

```
after typing            0 requests
after apply-to-all      0 requests   all ten .week-time fields now read 10:00 / 18:00
after save              1 request    PUT /api/v1/admin/patterns/{id}/rules

09:00 17:00 x5   ->   10:00 18:00 x5
```

Two field entries and two presses, and **one `PUT`, not one per day**. The draft is the reason the apply issues nothing, and the endpoint storing the whole set is the reason the save issues one.

### 5. Opening and closing a day is one click each — pass

```
Remove Friday window 1   1 click, 0 requests   row reads "Friday  Closed  Open"
Open Saturday            1 click, 0 requests   row gains window 1, both fields empty
```

Empty rather than a working nine-to-five, which is the pane's stated safety property: hours nobody typed are hours nobody chose to offer. Both edits sit in the draft until the same *Save weekly hours* the check above counted.

### 6. The pattern's links, and what they offer, without scrolling at 1280×800 — pass

`.admin-shell` is exactly one viewport tall and `.admin-main` is what scrolls inside it, so "without scrolling" is a question about that element's box and not the document's. Read at `scrollTop 0`, untouched:

```
#preview-as       options "Days only", "Consulting call"      bottom 273.5
month grid                                                    bottom 630.5
.admin-main       scrollTop 0   clientHeight 800

first three squares after choosing the link:
  "Monday 17 August, open, 16 times available"
  "Tuesday 18 August, open, 16 times available"
  "Wednesday 19 August, open, 16 times available"
```

Which links stand on the pattern, and what one of them actually offers, both inside the first 800 pixels. **What is not inside them: 292 pixels of the pane, including the *Save weekly hours* button at y 983.** That is not what this check asks and it does not fail it; it is in the register at the end, because it is the more interesting number.

### 7. Two panes at 1280, stacked with the week first at 899 — pass

Measured rects, through Playwright.

```
1280x800   Weekly hours   x 272   y 100   612 x 960
           Preview        x 908   y 100   340 x 960     same y — one row

 899x900   Weekly hours   x  32   y  100  835 x 960
           Preview        x  32   y 1084  835 x 555     same x — one column,
                                                        and the week is the one on top
```

899 is one pixel below the 901 the stylesheet splits at, which is where a breakpoint is actually tested. The week is first in the source and therefore first when they stack, which is the whole of how that ordering is guaranteed.

### 8. Signed out, the editor is the locked screen and nothing else — pass

A browser with no cookie, straight at the editor's own address:

```
url          /admin/availability/{patternId}
h1           "Sign in to continue"
.admin-shell 0     .rail 0     .tabbar 0
.editor-cols 0     .days 0
buttons      ["Sign in"]
```

No rail, no tab bar, no page heading of its own, no empty month, no half-drawn frame. The whole document is the card and the one control on it.

### 9. Bundle — pass

`bash scripts/bundle-budget.sh` after `npm run build`:

```
bundle-budget: booking route 71879 bytes gzipped across 1 file(s), budget 76800
bundle-budget: lazy chunks 11155 bytes gzipped across 1 file(s), not charged to the budget
```

**4,921 bytes of headroom, 93.6% of the 76,800 ceiling consumed.** The budget is 76,800 and nothing else. The 61,440 — 60 KiB — that still gets quoted from before Part A was superseded during Part A itself, when the React 19.2 shell alone measured 61,641 gzipped and did not fit it; a figure that the framework alone exceeds is not a ceiling, and it has not been the one in `scripts/bundle-budget.sh` since.

What the seven commits themselves cost was measured rather than inferred: each one was extracted into a scratch tree with `git archive` and built with the same toolchain, so the walk below is eight builds and not an attribution.

```
                                   booking route          admin chunk
1a00119  #60, the commit before        71,789                 9,606
e31498d  #61  the client                71,786    -3           9,601      -5
b1e2e84  #62  MonthGrid day states      71,808   +22           9,604      +3
d4e9007  #64  the editor route          71,806    -2           9,861    +257
25adcca  #65  the list                  71,806     0           8,790  -1,071
3533525  #67  the week pane             71,808    +2           9,666    +876
3ee73e6  #68  the preview               71,879   +71          11,155  +1,489
─────────────────────────────────────────────────────────────────────────────
this work                                          +90                +1,549
```

**Ninety bytes on the invitee's route for the whole editor**, because everything else is behind the lazy admin chunk. All ninety are `MonthGrid`, which is the one file this work touched that the booking page also imports — `git diff --stat` over `src/lib` and `src/components` finds nothing else in either of the two commits that moved the number. #63 is not in the table because it is the backend's `PUT` and builds the same frontend as #62; #65 subtracting a kilobyte from the workbench is the 635-line page being deleted.

Part C recorded 71,471 at `244dffc`; the 318 bytes between that and `1a00119` belong to #55–#60 and not to this work.

### 10. The four operator actions — pass, and the before column was driven rather than quoted

The instance on 5302 runs the current backend behind the pre-rewrite frontend, so both columns are the same engine and the difference is the screen. Every "before" figure below came out of a browser driving that page.

```
                        before                                   after
block a date            1 date typed + 1 click, 1 PUT            1 click, 1 PUT
                        no calendar anywhere on the page         a square on the month
shift a five-day week   10 clicks + 7 field entries = 17         2 field entries + 2 presses
                        10 PUTs                                   1 PUT
close a day             1 click, 1 PUT, live immediately         1 click, 0 requests
open a day              1 select + 1 click, 1 PUT                1 click, 0 requests
what a link offers      nothing: 0 calendars, 0 link pickers,    1 select, and the month
                        0 counts on the whole screen              fills with that link's counts
```

Shifting the hours is the one that changes shape rather than degree. No rule on the old page was editable in place — the only controls were *Remove* and the add form underneath — so a five-day shift is five removals and five additions, and each of the ten is its own `PUT /rules` replacing the whole set. Ten writes to a live pattern, each one a moment where the week on offer is not the week the operator meant.

The screen itself, measured at 1280×800:

```
                              before      after
list card, collapsed          313.5 px    313.5 px
card with the editor open   1,292.9 px    — there is no editor in a card any more
  once it carries one exception  1,381.1 px
.admin-main scrollHeight      1,432 px      800 px   (clientHeight 800 — the list fits)
controls inside the open card    17           2      (Edit, Delete)
```

1,381 px is the ~1,400 the plan opened with, measured. The card grows 4.4× when it opens, and one open card overflows an 800 px viewport by 632 px.

## The register of what is not covered

**The week pane's one primary action is below the fold at 1280×800.** *Save weekly hours* sits at y 983 in an 800-pixel viewport, because the pane is 960 px tall and starts at 100. Every draft edit in checks 4 and 5 therefore ends with a scroll the counts above do not include. The check as written is about the links and what they offer, and that half passes; this is the half nobody asked about. Its bottom edge is 227 px past the fold at this viewport, and at the 900 px `measure-ui.mjs` uses for every other reading it would be 127 — 100 px closer and still under it.

**The `.day` square is under the Tap Floor, and the editor's is smaller than the booking page's.**

```
editor preview pane, 340 px wide     39.14 x 40
booking page calendar, 300 px wide   40.00 x 40
DESIGN.md Tap Floor                  44
```

This is the booking page's pre-existing geometry and not this work's — the editor reuses the component — but the editor made it a second place the number is wrong, and it is 0.86 px worse there. Fixing it moves the invitee's calendar, which is why it is recorded rather than changed.

**Check 2 is a snapshot, not a property.** One month, one link, one set of limits, plus the single override case that opens a closed day. Nothing here would catch a drift that only appears at a start increment the fixture does not use, or on a link with a daily cap. A generative comparison — random rules, random overrides, `patternDays` against the endpoint for the same month — is the version of this check that would keep holding, and it does not exist.

**The old page's numbers are one operator's route through it, not its cheapest.** The seventeen interactions in check 10 assume the removals happen before the additions, and they must — the set with both windows on it was sent to check:

```
PUT /admin/patterns/{id}/rules  {Mon 09:00–17:00, Mon 10:00–18:00}
  -> 409  rule_overlaps
     "Another window on that day already covers part of those hours."
```

Within that constraint the order taken is the shortest one there is, and the two time fields were typed once rather than five times because the add form keeps their values between adds.

**Nothing here was measured on a phone.** Check 7's 899 px is the stacked layout at a desktop height; the editor at 440×956, where the tab bar is drawn and `.admin-main` under-reserves for it by the six pixels Part C recorded, has not been looked at.

**Idle memory was not re-measured.** Part C's 190.2 MiB against a target of under 100 Mi is the last reading, and this work adds no hosted service, no client and no dependency to the process. The frontend does not enter it.

---

# Operator cancel and propose

## Why this section exists

Only to correct a number this work moved. The last recorded reading for the invitee's first load was 71,879 bytes gzipped, and that figure has been the one quoted since; this work added a route the entry chunk carries, so the figure is now wrong for a reason that belongs to this change rather than to drift. Nothing else here is re-audited — the ten checks, the seven design conditions and the register above all still describe the runs that produced them.

## Bundle

Both ends measured with `npm run build && bash scripts/bundle-budget.sh`, the base built in a throwaway worktree with the same toolchain so the two are comparable:

```
                                  booking route          admin chunk
5236698  the commit before           71,904                13,858
HEAD     cancel and propose          72,727    +823        15,085   +1,227
─────────────────────────────────────────────────────────────────────────
                        budget       76,800   (4,073 free, 94.7% used)
```

**The invitee pays 823 bytes for a route they may never open, and that is deliberate.** `/proposal/:token` is where the "a new time is suggested" mail sends them, so `App.tsx` imports `Proposal` eagerly for the reason `/integrations` is imported eagerly: a link pasted into a message must not land on a chunk boundary. Making it lazy would move those bytes to a second request on the one route that exists to be opened from an email, which is the trade Part C already refused for the OAuth callback.

The operator's half — the picker, the row's controls and the admin HTTP calls — is the 1,227 bytes in the admin chunk and is charged to nobody: `routes/admin/` is reachable only through `lazy(() => import('./routes/admin/AdminShell'))`.

## The register of what is not covered

**The `propose-*` checks in `scripts/measure-ui.mjs` are run by hand and nothing gates them.** They caught two defects no unit test could see — a month grid collapsing to single-digit day squares inside a table cell, and a commit button hidden below 861px by a class that pairs with an action bar the admin shell does not have — and they hold those guarantees only as often as somebody runs them. `verify.sh` does not, and should not: they need a seeded instance, an operator session and a browser download.

**Nothing about the invitee's proposal page was measured in a browser.** The picker was, at six viewports; the page the invitee actually opens from the mail was not, and its own layout claims rest on unit tests and reading alone.

---

# Personal links

## Why this section exists

Same reason as the section above it: to correct the one number this work could move, and nothing else. The last recorded reading was 72,727 bytes gzipped on the booking route with a 15,085-byte admin chunk beside it, taken at `dc7c89e`, and that pair has been the quoted figure since. It is stale for two reasons at once — twenty-one merges landed on `main` in between, and this work put a second admin route behind the section switch — so a reading that does not separate the two would be worse than no reading at all.

## Bundle

Measured 2026-08-28 with `npm run build && bash scripts/bundle-budget.sh`, the base built at this branch's own merge base in a throwaway worktree sharing this one's `node_modules`, the lockfile being byte-identical across the range:

```
                                  booking route          admin chunk
a1ba560  the branch point            73,216                17,751
568a893  personal links              73,217        +1      17,950     +199
─────────────────────────────────────────────────────────────────────────
                        budget       76,800   (3,583 free, 95.3% used)
```

**The invitee pays one byte.** The panel, the link picker, the deadline and the section nav are all under `routes/admin/`, so all of them land in the lazy chunk that no invitee fetches; the single byte is gzip's own accounting shifting under a changed dictionary, not a line of code the booking route runs.

**The gap between 72,727 and 73,216 is not this work's.** Those 489 bytes belong to the twenty-one merges in `dc7c89e..a1ba560` — among them #129 and #131, which built the personal link and the page this branch puts a switch in front of. The branch point is measured here rather than assumed so that the +1 above reads as the whole of this change and not as a slice of a larger move.

## The register of what is not covered

**No rail item is marked current on `/admin/links/invites`.** The section switch marks itself correctly — `aria-current="page"` moves between its two options — but the shell's own navigation does not: at 1280 the rail's *Links* entry loses its raised pill on the sub-route, and at 899 the tab bar's *Links* loses its underline and its accent. Measured on the running instance, `.rail .nav a` carries no `aria-current` at all there, where `/admin/links` gives it to *Links*. The switch is a second level of navigation under a rail item that stops claiming the operator is inside it, and nothing in the gate for this work asked about the level above.

**The header was never measured below 759px.** It holds one row with room to spare at all three admin widths, and the design doc names crowding as this shape's cost, so the width where the heading and the switch actually collide is the interesting one and it is under every viewport measured here.
