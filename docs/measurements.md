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

`bash scripts/bundle-budget.sh` is the only place the budget number lives, and it weighs everything the build writes into `wwwroot/assets/` — JavaScript and CSS alike.

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
