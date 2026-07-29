# Attendance API — Mobile Developer Handoff

**Status: partial contract freeze, 2026-07-29. Device authentication and the `code` field are now
LIVE.** Revised after Phase 4c shipped.

> **⚠ If you read the previous revision of this document, re-read §1 and §2.**
>
> - §1: you no longer have the option of sending no header. **4b has landed** — the three capture
>   endpoints *require* a device key, and a client built to the older text gets `401` on every tap.
> - §2: **4c has landed.** Every tap response now carries `code` and `serverTime`, and a rejected tap
>   is an `application/problem+json` body rather than a `TapResult` with `success: false`. If you
>   were reading `success` off a `4xx` body, that field is gone from those responses — read `code`.
> - Also new in 4c, and it will refuse taps a previous build accepted: a `tappedAt` more than five
>   minutes ahead of our clock, or outside the event's window, is now a `400`. See the last of the
>   three rules further down.

This document exists so the mobile capture app can start now instead of waiting for Phase 4 to be
built. It describes what the backend *actually does today*, verified against the source — plus the
parts of the contract that are now **frozen** and can be built against before they exist.

**Read this first:** three things are frozen and will not change. Everything else in this document is
provisional and may move. Build your queue against the frozen parts; stub the rest.

**Scope split.** The React Native capture app and the RFID hardware adapters belong to the mobile
developer. This repo owns and publishes the API they consume. There is no physical-tap milestone on
our side; taps are testable over plain HTTP because a card UID is just a student number.

---

## FROZEN — build against these now

These three are settled and will not change shape. **§1 (device auth) is implemented and enforced as
of Phase 4b; §2 (`code`, `serverTime`, problem bodies) is implemented as of Phase 4c**, apart from the
two batch-only tokens noted in its table. §3 is still being built — build against it anyway.

### 1. Device authentication header

```
Authorization: DeviceKey eams_dk_<keyId>_<secret>
```

A standard `Authorization` header with a `DeviceKey` scheme — **not** a bespoke `X-Api-Key`. The key
is issued once by the admin back office and never retrievable afterwards. Store it in
`expo-secure-store` / Android Keystore — **never `AsyncStorage`**.

The planned enrolment UX is a QR code rendered on the admin device screen at issue time, which your
app scans. Tell us if you would rather have something else; nothing is built yet.

Your key is scoped to `attendance.capture` **only**, and it authenticates exactly these endpoints:

| Endpoint | Requires the key |
|---|---|
| `POST /attendance/tap` | **yes, now** |
| `POST /attendance/tap/batch` | **yes, now** |
| `GET /students/by-card/{cardUid}` | **yes, now** |
| `POST /devices/{id}/heartbeat` | **yes, now** |
| `GET /attendance/live/{eventId}` | **no** — see below |

`GET /attendance/live/{eventId}` deliberately takes **no device key**. Your key is scoped to
`attendance.capture`, which is a *write* permission; requiring it to *read* a dashboard would put a
write-capable credential into every browser watching an event. It stays open with the rest of the
admin surface until Phase 6. If your app polls it, send no `Authorization` header.

Everything else on the API remains open for the moment. **This is a narrowing of the open surface,
not a security boundary** — see the note at the end of this section.

### Authentication failures

| `code` | HTTP | Meaning |
|---|---|---|
| `DeviceKeyMissing` | 401 | No `Authorization: DeviceKey …` header |
| `DeviceKeyMalformed` | 401 | Header present but not a well-formed token |
| `DeviceKeyInvalid` | 401 | Unknown key id, or the secret does not match |
| `DeviceKeyRevoked` | 403 | The key was burned. Get a new one issued |
| `DeviceInactive` | 403 | The device record was retired |
| `RateLimited` | 429 | Too many capture requests. Honour `Retry-After` |

`DeviceKeyInvalid` deliberately does **not** distinguish "no such key" from "wrong secret" — it would
otherwise confirm a valid key id to someone who does not hold the secret.

> **What this does and does not protect.** Device auth makes the *capture channel* attributable,
> which is what the audit story needs. It does **not** make the system safe to expose: the admin
> surface is still open until Phase 6, so this API must stay on a local or trusted network. Nothing
> about your client changes because of that — it is stated so you are not surprised later.

### 2. Outcome tokens — the machine-readable `code` field

Every tap response and every batch row carries a stable `code`. **Branch on this, never on
`message`.** The token list is frozen contract; a rename on our side would be a breaking change and is
pinned by a test that fails the build (`TapOutcomeContractTests`, added with the field in 4c).

**Live as of Phase 4c** for `POST /attendance/tap` and `POST /attendance/manual`, on success bodies and
problem bodies alike. The two rows marked ⏳ below belong to `POST /attendance/tap/batch` and arrive
with it in 4d; the pin test knows they are not on the wire yet and will require them the moment the
endpoint exists.

| `code` | HTTP | Meaning | Your queue |
|---|---|---|---|
| `Recorded` | 200 | New attendance row written | Drop |
| `DuplicateIgnored` | 200 | Your retry was absorbed — same `deviceTapId` already landed | Drop |
| `CheckedOut` | 200 | `TimeInOut` check-out recorded | Drop |
| `AlreadyRecorded` | 200 | Student already had a record; nothing changed | Drop |
| `EventNotFound` | 404 | — | Stop retrying |
| `CardNotFound` | 404 | No active card matches that UID | Stop retrying |
| `DeviceNotRegistered` | 404 | Unknown device, or device belongs to another school | Stop retrying |
| `EventNotOpen` | 400 | Event is Draft, Closed or Cancelled | Stop retrying |
| `DeviceMismatch` | 400 | Body `deviceId` disagrees with your authenticated key | Stop; bug on your side |
| `DeviceTapIdRequired` | 400 | Batch row's `deviceTapId` is unusable — absent, blank, over 100 characters, or the row itself was `null` | Stop; bug on your side |
| `TappedAtOutOfRange` | 400 | `tappedAt` more than 5 min in the future — check your clock | Stop; resync clock |
| `TappedAtOutsideEventWindow` | 400 | `tappedAt` outside the event's window | Stop retrying |
| `BatchTooLarge` | 400 | Batch-level only; chunk and resend. Limit is in `maxBatchRows` | Chunk, retry |
| `InvalidCursor` | 400 | Live endpoint: `since` was not a cursor we issued. **Refused, not silently downgraded to a snapshot** | Re-poll with no `since` |

Tap and manual-override **failures are** RFC 7807 problem bodies carrying the same `code`, matching the
rest of the API — `application/problem+json`, with `title`, `detail`, `status`, `traceId`, `code` and
`serverTime`. One accessor — `body.code` — works across success and failure alike.

### 3. Batch sync shape

```jsonc
// POST /attendance/tap/batch
{
  "clientClockAt": "2026-07-29T09:14:03Z",   // your device clock at send time
  "taps": [ /* the same TapRequest row shape as POST /attendance/tap */ ]
}
```

```jsonc
// 200 — always, for any well-formed batch
{
  "accepted": 47,
  "rejected": 3,
  "serverTime": "2026-07-29T09:14:05Z",
  "results": [
    { "index": 0, "deviceTapId": "a7f3…", "code": "Recorded",     "status": 200, "record": { /*…*/ } },
    { "index": 1, "deviceTapId": "b8c4…", "code": "CardNotFound", "status": 404, "record": null,
      "message": "…" },
    { "index": 2, "deviceTapId": "c9d5…", "code": "DuplicateIgnored", "status": 200, "record": { /*…*/ } }
    // …one entry per tap you sent, in the order you sent them
  ]
}
```

**`results` is dense, not sparse** — one entry per submitted tap, `accepted + rejected == results.length`.
The example above shows three of a batch of three; the counters in the envelope are illustrative of a
larger flush.

Rules you can rely on:

- **The batch HTTP status describes the batch; each row's `status` describes that tap.** We are not
  using 207 Multi-Status — proxies and clients handle it inconsistently.
- **On a `5xx`, retry the whole batch — that is always safe.** ⚠ **Corrected 2026-07-29:** an earlier
  revision of this document said a `5xx` means *nothing was committed*. That was wrong, and it
  contradicted the per-row processing described two bullets down. Rows are committed individually, so
  a server error partway through leaves the earlier rows written.
  **Your action does not change** — retry the whole batch — but the reason matters: it is safe because
  **every row is idempotent**, not because the batch is atomic. Already-written rows come back
  `DuplicateIgnored` on the retry. Never assume a `5xx` means you can start over from a clean slate.
- **Batch-level `4xx`** is transport/auth/size only: `400` malformed or `BatchTooLarge`, `401` missing
  or bad key, `403` revoked key.
- **Rows correlate by both `index` and `deviceTapId`**, and `results` comes back in **request array
  order** — `results[i].index == i`. Zip positionally if that is easier; the `index` field is there so
  you do not have to trust that.
- **`eventId` stays per row**, not hoisted — a device that switched events while offline can flush a
  mixed batch.
- **`deviceTapId` is REQUIRED on this endpoint** (optional on single `/tap`). A queued tap without an
  idempotency key cannot be safely retried.
- **We sort rows by `tappedAt` ascending server-side**, ties by array index. Send chronologically if
  you can; an out-of-order queue is safe either way.
- An empty `taps` array is a 200 with empty results, not an error.

> **The one thing sorting cannot fix.** A check-out whose check-in was in an *earlier batch that
> failed* still lands as a check-in. So: **flush strictly in order, and stop the queue at the first
> retryable error** rather than skipping past it.

**Batch size cap: 200 rows — enforced.** No longer provisional. A larger batch is refused whole with
`BatchTooLarge`, never truncated, and the limit comes back as `maxBatchRows` in the problem body so
you can chunk from the response rather than hard-coding it.

**`clientClockAt` is consumed, never a reason for refusal.** We compare it against our clock to
measure your device's drift and log it. It cannot fail your batch.

---

## Reaching the API

| | |
|---|---|
| Base URL (local dev) | `http://localhost:5080/api/v1` |
| Interactive docs | Swagger UI at `http://localhost:5080/` — **Development only** |
| Serialisation | JSON, camelCase field names |
| Errors | RFC 7807 `application/problem+json`, every body carrying a `traceId` you can quote back |

`Guid` values are JSON strings (UUID). All `DateTime` values are **UTC**, ISO 8601.

---

## Endpoints that exist today

### `POST /attendance/tap`

```jsonc
// TapRequest
{
  "eventId":     "3f2504e0-4f89-11d3-9a0c-0305e82c3301",  // required
  "cardUid":     "USA39912",   // required; normalised server-side
  "deviceId":    null,          // guid | null — omit it. Cross-checked against your key; a
                                //   mismatch is 400 DeviceMismatch, never a silent ignore
  "deviceTapId": "a7f3…",      // YOUR idempotency key. Always send one.
  "tappedAt":    "2026-07-29T01:15:00Z"  // null means "now, on the server"
}
```

```jsonc
// TapResult — success bodies only; a failure is a problem body (FROZEN §2)
{
  "success":    true,
  "message":    "…prose, do not parse…",
  "record":     { /* AttendanceDto | null */ },
  "code":       "Recorded",               // branch on this
  "serverTime": "2026-07-29T05:31:22.117Z" // your clock offset comes from here
}
```

`success` is retained for the clients already reading it and is now redundant with `code`: it is
`false` on exactly the outcomes that arrive as a `4xx`.

### Others

| Endpoint | Returns | Note |
|---|---|---|
| `GET /students/by-card/{cardUid}` | `StudentDto` \| 404 | UID→student for the scan screen |
| `GET /attendance?eventId=&studentId=&status=` | `AttendanceDto[]` | all filters optional |
| `POST /attendance/manual` | `TapResult` | organiser override, **not** a device path |
| `GET /events?status=Open`, `GET /events/{id}` | `EventDto` | |
| `GET /events/{id}/roster` | `EventRosterDto` | expected-vs-present |
| `GET /events/{id}/summary` | `EventSummaryDto` | headline counts |

---

## DTO reference

Field names are camelCase on the wire. `?` marks nullable.

### `AttendanceDto`
```
id, eventId, studentId   guid
studentName              string
studentNumber            string
checkInAt                datetime?
checkOutAt               datetime?
status                   string   // Present | Late | Absent | Excused
captureMethod            string   // Rfid | Manual | Import
```

### `StudentDto`
```
id             guid
studentNumber  string
fullName       string
firstName      string
middleName     string?
lastName       string
email          string?
gender         string?
photoUrl       string?
status         string    // Active | Inactive | Graduated
cards          CardDto[]
course         string?   // ⚠ derived display cache
yearLevel      string?   // ⚠ derived display cache
section        string?   // ⚠ derived display cache
```

> **`course` / `yearLevel` / `section` are a derived cache, not the truth.** A student can sit in
> several sections at once — 12 of 52 in the real roster do — so these single-valued fields cannot
> represent reality. Display only. **Never filter or group by them.**

### `CardDto`
```
id        guid
cardUid   string   // normalised: uppercase, separators stripped
label     string?
isActive  bool
```

### `EventDto`
```
id                  guid
name                string
description         string?
location            string?
startAt, endAt      datetime   // UTC
attendanceMode      string     // Single | TimeInOut
graceMinutes        int        // Present vs Late boundary, from startAt
requireRegistration bool
status              string     // Draft | Open | Closed | Cancelled
```

### `EventSummaryDto`
```
eventId         guid
eventName       string
expected        int      // the invited population — NOT the number who tapped
present, late, absent, excused   int
unexpected      int      // walk-ins: recorded but never invited
attendanceRate  double   // % of the invited who turned up, 1dp. Cannot exceed 100.
```

### `EventRosterDto` / `EventRosterEntryDto`
```
EventRosterDto
  eventId, eventName, status
  isFrozen     bool   // true once the audience is snapshotted (Closed or Cancelled)
  expected, present, late, absent, excused, notRecorded, unexpected  int
  entries      EventRosterEntryDto[]

EventRosterEntryDto
  studentId      guid
  studentNumber  string
  fullName       string
  section        string?
  isExpected     bool     // false = walk-in
  status         string?  // null = expected but no record yet
  checkInAt, checkOutAt  datetime?
  captureMethod  string?
```

---

## Three rules to implement on your side

### Card UIDs: normalise before you compare

**A card UID *is* the student number (REGNO).** No tap-to-bind screen is needed — students arrive
card-ready from the roster import.

Normalisation is uppercase with all non-alphanumerics stripped: `04:A7:B8:C9`, `04-a7-b8-c9` and
`04a7b8c9` are **one card**, stored as `04A7B8C9`. The server normalises inbound, so you may send the
raw reader format — but normalise before any *local* cache or comparison, or your dedupe will
disagree with ours.

> **Security note, for awareness rather than code.** Student numbers are sequential and printed on the
> ID, so a UID is guessable. Device auth, the event window, and audit are the mitigations — which is
> why device keys are not optional.

### `deviceTapId`: always send one, keep it stable, keep it **under 100 characters**

The idempotency key the whole offline design rests on. Generate it once, when the tap happens on the
device, and **never regenerate it on retry**. Required on batch, strongly recommended on single taps.

> **⚠ Maximum length: 100 characters.** This is now published because you have not yet settled your
> generation scheme, and a composite like `{installId}:{eventId}:{counter}:{uuid}` passes 100 easily.
> An over-length value is refused as `DeviceTapIdRequired` / 400 for that row — a client bug, not a
> transient error, so **do not retry it**; fix the generator.
>
> A UUID (36 chars), or a UUID prefixed with a short install id, fits comfortably. If your scheme
> cannot fit, tell us — widening the column is a migration, not a redesign, and it is far cheaper to
> do before you ship than after.

### Everything is UTC — and your clock is load-bearing

`tappedAt` plus the event's `graceMinutes` is what decides **Present vs Late**. Send UTC with an
explicit `Z`. A bare or offset-bearing local time misjudges the boundary by the server's offset —
eight hours in Manila. This has already been a real defect on our side once.

**We will never silently rewrite your `tappedAt`.** Instead:

- Every response carries `serverTime`. Compute an offset and apply it before enqueueing.
- Send `clientClockAt` on each batch — your clock at send time. Its difference from our clock is pure
  skew, free of queue latency, and we log and alert on drift.
- A `tappedAt` **more than 5 minutes in the future** is rejected (`TappedAtOutOfRange`). The past is
  never age-limited — an old queued tap is the entire point of the endpoint.
- A `tappedAt` outside the event window — default **60 minutes either side** of `StartAt`/`EndAt`,
  configurable per school — is rejected (`TappedAtOutsideEventWindow`). *Submission* lateness is
  unconstrained; only the claimed tap time is checked. Both bounds are inclusive.
- **Omitting `tappedAt` is not a way around either check.** `null` means "now, on the server", and
  that instant is validated against the event window exactly like a value you sent. A tap on an event
  that finished yesterday is a `400` whether or not you name a time.

> **Always send `tappedAt` — not only `deviceTapId`.** This is the one recommendation in this document
> that exists because of a retry consequence rather than a rule.
>
> With `tappedAt: null`, the instant being validated is **re-derived from our clock on every attempt**.
> So a tap that landed at 10:00 whose response you lost, retried at 12:30 after the event's window has
> closed, comes back `400 TappedAtOutsideEventWindow` — not the `DuplicateIgnored` you would expect for
> a tap that is already recorded. The stored attendance is correct and both codes tell your queue to
> drop the row, so nothing is lost; but reconciliation *by code* is wrong for that row.
>
> Sending an explicit `tappedAt` pins the value across every retry and the problem disappears.

**Live as of 4c.** Both refusals are real now, and they will refuse taps an earlier build accepted.
Neither ever alters the timestamp you sent: an accepted `tappedAt` is stored exactly as given, and an
unbelievable one is refused rather than clamped into range — clamping would invent an observation on
the field that decides Present vs Late.

---

## Live attendance: polling, not SignalR

**Decision taken 2026-07-29.** The plan named a SignalR hub; we are shipping a cursor-delta polling
endpoint instead, and you should build against polling.

The reason, so it does not look arbitrary: a SignalR client must fetch a snapshot on every reconnect
to close the gap it missed — so this endpoint is required under *both* designs. It is also far
kinder to a campus firewall, needs no backplane or sticky sessions on our side, and does not force
your device key into a WebSocket query string where it lands in access logs.

```
GET /attendance/live/{eventId}?since=<cursor>
```

- **`since` absent** → snapshot: `{ eventId, cursor, counters, entries[], hasMore, serverTime, pollAfterSeconds }`
- **`since` present** → `{ eventId, cursor, counters, changes[], hasMore, serverTime, pollAfterSeconds }`

> **`hasMore` is the one field you must not ignore.** A response carries at most **500** entries or
> changes. When `hasMore` is `true` the page was truncated and its `cursor` points at the last row you
> received — **poll again immediately rather than waiting `pollAfterSeconds`.** Otherwise a
> 5,000-attendee event takes ten polls at five seconds each to fill a dashboard, and it looks like the
> feed is broken rather than paging.

**Rate limited: 240 polls per minute, per client address** — roughly one poll every 250 ms, far above
the 5-second default. Exceeding it is the standard `RateLimited` / 429 with `Retry-After`. This limit
is partitioned separately from device-key capture limits, so a browser tab polling a dashboard can
never spend a kiosk's tap budget.

**Live as of Phase 4d.** Details now settled:

- **`pollAfterSeconds` defaults to 5**, server-configurable between 1 and 300. **Respect it** — it lets
  us back clients off under load without you shipping a release.
- **A cursor that is not well-formed is refused** — `400 InvalidCursor` — rather than silently
  downgraded to a snapshot, which would look like "nothing changed" forever while your cursor stayed
  broken. On `InvalidCursor`, re-poll with no `since` and take a fresh snapshot.
  To be precise about what is checked: we validate the cursor's **shape**, not that we issued it. A
  well-formed cursor from somewhere else decodes, and a cursor pointing beyond our current position is
  treated as a fresh snapshot rather than an error. **Send back the cursor from a previous response
  verbatim** and none of this concerns you.
- **Cursors are URL-safe** — 11 characters, base64url, no padding. They contain no `+`, `/` or `=`, so
  they are safe to concatenate into a query string by hand. (An earlier build issued 12-character
  standard base64; those are still accepted, so a client mid-upgrade is not stranded.)
- **`EventNotFound` is a 404** here as everywhere else.
- The delta objects carry §6.4's declared hub payload as their first six fields, verbatim.

If a hub is ever added, it will emit exactly the same delta object, so your reducer would not change.

---

## Not built yet

| Item | Status |
|---|---|
| Device registration + API keys | ✅ **shipped in 4b** |
| Rate limiting on capture endpoints | ✅ **shipped in 4b** |
| `code` + `serverTime` on tap responses | ✅ **shipped in 4c** |
| Check-out idempotency, clock skew, event window | ✅ **shipped in 4c** |
| `POST /attendance/tap/batch` | ✅ **shipped in 4d** |
| `GET /attendance/live/{eventId}` | ✅ **shipped in 4d** |
| Published OpenAPI document | Phase 4e — supersedes this file |
| Auth for everything else | Phase 6 (JWT + RBAC) |

---

## Open defects — both being fixed, both affect you

### 1. A `TimeInOut` check-out discards its own `deviceTapId` — ✅ **CLOSED in 4c**

Today a check-out returns `AlreadyRecorded` instead of `CheckedOut`, and its `deviceTapId` is not
retained. The fix gives a check-out its **own** idempotency key with its own unique index behind it,
so each half of a `TimeInOut` pair is independently retryable.

**What this means for you:** replaying a check-out returns `DuplicateIgnored`, and `CheckedOut` means
the check-out was newly recorded. Both halves of a `TimeInOut` pair are now independently retryable, so
send a fresh `deviceTapId` with each tap and keep it stable across retries of *that* tap.

One residual, and it is broader than a `TimeInOut` edge case, so it should not surprise you: **any tap
that changes nothing** returns `AlreadyRecorded` without storing its `deviceTapId`, so replaying it
returns `AlreadyRecorded` again rather than `DuplicateIgnored`.

In `Single` mode — the default — that is the *ordinary* duplicate path, not a rare one: every second
tap of the same student at the same event lands here. Both codes mean "drop it", so your queue behaves
identically either way; it is only reconciliation *by tap id* that cannot see those taps. If your
client reasons about "did tap X land?" purely from tap ids, treat `AlreadyRecorded` as an answer it
will never get.

### 2. A device from another school can record a tap — ✅ **CLOSED in 4b**

Device authentication was itself the fix: your key identifies the device, its school scopes the event
lookup, and a foreign event is `EventNotFound`. An explicit device-vs-event school check backs it up,
reported as `DeviceNotRegistered` / 404 so the API never confirms that a device exists in another
school. No change on your side.

---

## What we still need from you

Four of these were designed *around* rather than *from*, because you were waiting on us. If any answer
contradicts what is frozen above, tell us **now** — the batch shape, the required `deviceTapId`, and
the clock-skew rules are the parts most likely to move.

1. **Offline queue semantics** — retry/backoff policy, how long a tap may sit queued, what you do with
   a permanently rejected row.
2. **`deviceTapId` generation** — what it derives from, and its uniqueness guarantee. **Now urgent:**
   see the 100-character limit above. If your scheme does not fit, we would rather widen the column
   before you ship than after.
3. **Batch size** — does 200 rows suit your flush strategy? The cap is enforced now, not provisional.
4. **Device enrolment** — is QR-scan-at-issue the UX you want? What should happen on reinstall?
5. **Clock skew** — how do you correct or flag a drifted device clock?
6. **Did anything you had already built depend on tap *failures* returning `TapResult` rather than a
   problem body?** That changed in 4c and you were the only consumer we could not check. If it broke
   something, tell us — `success: false` bodies are gone from the `4xx` responses.
7. **A row-level server-error token — we need your view before 4e freezes the OpenAPI.** Every token in
   §2 today describes a *decision* we made about a tap. There is none for "this row threw
   unexpectedly", so an unforeseen server-side failure on a single row can only surface as a
   **batch-level `5xx`** — which you are told to retry, forever, with nothing telling you which row is
   poison. We would rather add something like `RowFailed` (batch still 200, row `status` 500) than
   leave that gap. **Do you want it, and would your queue treat it as retryable or as poison?** Your
   queue's semantics should decide that, not ours. Adding it before publication costs nothing; adding
   it afterwards is additive but awkward.

---

## How this document changes

Phase 4e replaces it with a published OpenAPI contract; this file then becomes a pointer to it.
Anything marked FROZEN will not change before then. Anything marked ⚠, provisional, or "not built" may.

Questions to the backend team. Quote the `traceId` from any error body you want us to look into.
