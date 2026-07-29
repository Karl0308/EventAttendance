# Attendance API — Mobile Developer Handoff

**Status: DRAFT — not the published contract.** Written 2026-07-29, during Phase 3b-1.

This document exists so the mobile capture app can start now instead of waiting for Phase 4.
It describes what the backend *actually does today*, verified against the source — not what the
Technical Plan intends. Where the two differ, this file is right about today and the plan is right
about the destination.

**What this is not:** the published contract. Phase 4 publishes that, as an OpenAPI document, and it
will supersede this file. Two endpoints this app depends on do not exist yet, and two known defects
in the tap path are still open — both are listed below. Do not treat anything here as frozen.

**Scope split.** The React Native capture app and the RFID hardware adapters belong to the mobile
developer. This repo owns and publishes the API they consume. There is no physical-tap milestone on
our side; taps are testable over plain HTTP because a card UID is just a student number (see
[Card UIDs](#card-uids-normalise-before-you-compare)).

---

## Reaching the API

| | |
|---|---|
| Base URL (local dev) | `http://localhost:5080/api/v1` |
| Interactive docs | Swagger UI at `http://localhost:5080/` — **Development only**, deliberately not served in Production |
| Serialisation | JSON, camelCase field names |
| Errors | RFC 7807 `application/problem+json`, every body carrying a `traceId` you can quote back to us |
| Auth | **None. Every endpoint is currently open.** See [Auth](#auth-does-not-exist-yet) |

`Guid` values are JSON strings (UUID). All `DateTime` values are **UTC**, ISO 8601.

---

## Endpoints that exist today

These are implemented, tested against real SQL Server, and safe to build against — subject to the
caveats in [Open defects](#open-defects-that-affect-your-design).

### Capture

#### `POST /attendance/tap`

The core capture path. Permission (once auth lands): `attendance.capture`.

```jsonc
// request — TapRequest
{
  "eventId":     "3f2504e0-4f89-11d3-9a0c-0305e82c3301",  // required
  "cardUid":     "USA39912",   // required; normalised server-side
  "deviceId":    null,          // guid | null — see Device registration
  "deviceTapId": "a7f3...",    // string | null — YOUR idempotency key. Always send one.
  "tappedAt":    "2026-07-29T01:15:00Z"  // datetime | null; null means "now" on the server
}
```

```jsonc
// response — TapResult
{
  "success": true,
  "message": "Juan Dela Cruz checked in (Present).",  // human-readable, WILL be reworded
  "record": { /* AttendanceDto, or null when nothing was recorded */ }
}
```

#### `POST /attendance/manual`

Organiser override, **not** a device path. Permission: `attendance.write` — deliberately distinct
from `attendance.capture`, so a device key cannot rewrite a status. Query parameters:
`eventId`, `studentId`, `status` (default `Present`), `notes`.

This endpoint keeps working on a **closed** event, on purpose — it is the only sanctioned route to
correct a terminal event's record.

#### `GET /students/by-card/{cardUid}`

UID → student resolution for the scan screen. Returns `StudentDto` or 404.

Scoped `attendance.capture` rather than `students.read` on purpose: a device key must be able to
resolve one card without being trusted to browse the whole roster.

#### `GET /attendance?eventId=&studentId=&status=`

Returns `AttendanceDto[]`. All three filters optional.

### Event context

| Endpoint | Returns |
|---|---|
| `GET /events?status=Open` | `EventDto[]` |
| `GET /events/{id}` | `EventDto` |
| `GET /events/{id}/roster` | `EventRosterDto` — expected-vs-present, the absentee source |
| `GET /events/{id}/summary` | `EventSummaryDto` — the headline counts |

---

## Tap outcomes → HTTP status

**Branch on the HTTP status, never on `message`.** The message text is written for humans and will
be reworded without notice.

| HTTP | Outcomes folded into it | What your queue should do |
|---|---|---|
| `200` | `Recorded`, `DuplicateIgnored`, `CheckedOut`, `AlreadyRecorded` | Delivered. Drop from the queue. |
| `404` | `EventNotFound`, `CardNotFound`, `DeviceNotRegistered` | Stop retrying. Surface to the operator. |
| `400` | `EventNotOpen` | Stop retrying. The event is Draft, Closed or Cancelled. |
| `5xx` | — | Transient. Retry with backoff. |

> ### ⚠ Known gap: the outcome is not on the wire
>
> All four success outcomes return `200` with `success: true`. There is currently **no
> machine-readable field** distinguishing `Recorded` (we wrote a new record) from `DuplicateIgnored`
> (your retry was absorbed) from `AlreadyRecorded` from `CheckedOut`. The only signal is the prose
> `message`, which you must not parse.
>
> If your reconciliation needs that distinction — and an offline queue usually does — **tell us**.
> We added a stable `code` field to the students surface in Phase 3b-1 for exactly this reason, and
> the tap path should get the same treatment before Phase 4 publishes it externally. This is a
> cheap change now and a breaking one later.

---

## DTO reference

Field names are camelCase on the wire. `?` marks nullable.

### `AttendanceDto`
```
id             guid
eventId        guid
studentId      guid
studentName    string
studentNumber  string
checkInAt      datetime?
checkOutAt     datetime?
status         string   // Present | Late | Absent | Excused
captureMethod  string   // Rfid | Manual | Import
```

### `StudentDto`
```
id             guid
studentNumber  string
fullName       string
email          string?
course         string?   // ⚠ derived display cache — see below
yearLevel      string?   // ⚠ derived display cache
section        string?   // ⚠ derived display cache
status         string    // Active | Inactive | Graduated
cards          CardDto[]
```

Landing additively in Phase 3b-1 (in review as this is written):
`firstName`, `middleName?`, `lastName`, `gender?`, `photoUrl?`.

> **`course` / `yearLevel` / `section` are a derived cache, not the truth.** A student can sit in
> several sections at once — 12 of 52 in the real roster do — so these single-valued fields cannot
> represent reality. Read them for display only. **Never filter or group by them**, and never send
> them back on a write: the API refuses that with `400` and `code: "FieldIsDerived"`.

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
startAt             datetime   // UTC
endAt               datetime   // UTC
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
present         int
late            int
absent          int
excused         int
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
  status         string?  // null = expected but no record yet (the live absentee signal)
  checkInAt      datetime?
  checkOutAt     datetime?
  captureMethod  string?
```

---

## Three rules to implement on your side

### Card UIDs: normalise before you compare

**A card UID *is* the student number (REGNO).** No tap-to-bind screen exists or is needed — students
arrive card-ready from the roster import.

Normalisation is uppercase with all non-alphanumerics stripped: `04:A7:B8:C9`, `04-a7-b8-c9` and
`04a7b8c9` are **one card**, stored as `04A7B8C9`. The server normalises on the way in, so you may
send the raw reader format — but if you cache or compare UIDs locally, normalise first and compare
second, or your local dedupe will disagree with ours.

> **Security note, for your awareness rather than your code.** Student numbers are sequential and
> printed on the ID card, so a UID is guessable. Anyone who can reach the tap endpoint can forge a
> classmate's attendance. This is a property of the card scheme, not something either side can fully
> fix in software — which is why device API-key auth is a hard requirement before go-live, and why
> taps should be constrained to the event window and audited.

### `deviceTapId`: always send one, and keep it stable

This is the idempotency key the whole offline sync design rests on. Send it on **every** tap, online
or queued. A retry carrying the same `deviceTapId` is absorbed rather than duplicated — that is what
makes it safe for your queue to retry aggressively after a network failure.

Generate it once, when the tap happens on the device, and never regenerate it on retry.

### Everything is UTC

`startAt`, `endAt`, `tappedAt`, `checkInAt`, `checkOutAt` are all UTC. Send `tappedAt` as UTC with an
explicit `Z`.

A bare or offset-bearing local time gets compared against a UTC `startAt` and **misjudges Present vs
Late by the server's offset — eight hours in Manila.** This has already been a real defect on our
side once. `tappedAt` plus the event's `graceMinutes` is what decides Present vs Late, so your device
clock is load-bearing; tell us how you intend to handle clock skew.

---

## Not built yet — do not design around these

| Missing | Why it matters to you |
|---|---|
| `POST /attendance/tap/batch` | **The offline sync endpoint. Your most important one.** Phase 4. |
| `GET /attendance/live/{eventId}` | Live attendance feed. Phase 4. |
| SignalR hub `/hubs/attendance` | Real-time push. Phase 4. |
| Device registration + API keys | `TapRequest.deviceId` and a `DeviceNotRegistered` outcome exist, but nothing issues or registers a device. Phase 4. |
| Auth of any kind | See below. |

### Auth does not exist yet

**Every endpoint is currently open.** This is deliberate and recorded (ADR-001 D-6); JWT plus
permission-based RBAC is Phase 6.

The permission each endpoint *will* demand is already declared in code, so nothing will be
re-derived later — `attendance.capture` for taps and card lookup, `attendance.write` for manual
overrides. Your device key will be scoped to `attendance.capture` alone.

**Build an auth header seam into your HTTP layer now.** Adding one later is far more disruptive than
leaving an unused hook in place.

---

## Open defects that affect your design

Both are written as executable, reviewed, currently-skipped tests in `EAMS.Tests/KnownDefectTests` —
the expected behaviour is already specified, so closing them is "delete one `Skip` and make it
green" rather than "work out what correct means".

### 1. A `TimeInOut` check-out discards its own `deviceTapId`

It returns `AlreadyRecorded` instead of `CheckedOut`. **This is squarely in your path**: the
idempotency key we publish as the foundation of offline sync is not honoured on the check-out half
of a `TimeInOut` event. The test's own note says it should be settled before Phase 4.

**If you build a check-out queue on `deviceTapId` idempotency today, you are building on something
known-broken.** No data loss occurs, but the contract lies. Flag your dependency on this and we will
prioritise it.

### 2. A device from another school can record a tap

Latent while the system is single-tenant, so it cannot bite today. It must close before multi-tenancy
becomes real. Listed for completeness — it should not change your design.

---

## What we need from you

So Phase 4 designs the batch endpoint around your actual client rather than our guess:

1. **Offline queue semantics** — retry and backoff policy, how long a tap can sit queued, what you do
   with a permanently rejected one.
2. **`deviceTapId` generation** — what it is derived from, and its uniqueness guarantee.
3. **Batch size and shape** — how many taps per `POST /attendance/tap/batch`, and whether you need
   per-row results or a single accept/reject. (We will give you per-row; confirm.)
4. **Device identity** — how you want a device registered and keyed, and what happens on reinstall.
5. **Clock skew** — how you correct or flag a device clock that has drifted, given `tappedAt` decides
   Present vs Late.
6. **Whether you need the machine-readable tap outcome** described in the gap note above.

---

## How this document changes

Phase 4 replaces it with a published OpenAPI contract; at that point this file becomes a pointer to
that document. Until then it tracks the code, and anything marked ⚠ or "not built" is subject to
change without a deprecation path.

Questions and answers to the backend team. Quote the `traceId` from any error body you want us to
look into.
