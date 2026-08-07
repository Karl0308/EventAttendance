# Attendance API — Mobile Developer Companion

**Status: the contract now lives in the generated OpenAPI document, not in this file (Phase 4e,
2026-07-30).**

> **New 2026-08-01 — `GET /events/{id}/manifest`, the offline capture cache.** A new endpoint, not a
> change to an existing one: nothing you have already built is affected, and you can adopt it whenever
> suits you. Its shape is in the generated document; the behaviour your client has to implement is in
> [The event manifest](#the-event-manifest-your-offline-cache) below.

> **⚠ If you have been building from an earlier revision of this file, read this section.**
>
> Everything that describes a *shape* — endpoints, payload fields, outcome tokens, status codes, the
> device-auth header, the batch envelope — has moved to the generated document and was **deleted from
> here** rather than left to rot. That text was maintained by remembering to; nothing failed when an
> edit was missed, so a stale contract looked exactly like a current one. The published document is
> generated from the source, so it cannot disagree with the running build.
>
> Nothing about the wire format changed on 4e. If you had built against the previous revision and it
> worked, it still works. This is a documentation change, with one fix: `POST /attendance/manual` now
> correctly advertises a problem body for its `400`/`404` instead of `TapResult`.

## Where the contract is

**`docs/api/openapi.json`, in this repo, beside this file.** That is the one to generate your client
from — you do not need to run our backend to get it. It is exported from a running build, so it is the
document that build actually serves, not a hand-maintained copy.

If you *are* running the backend locally:

```
GET /swagger/v1/swagger.json     the same document, live
GET /                            Swagger UI, browsable (development environments only)
```

The UI is deliberately not served outside development: until Phase 6 lands human authentication, an
unauthenticated description of an API whose admin surface is open hands out the map as well as the
door. The **document** builds in every environment, so client tooling is never blocked.

> **If `openapi.json` and the running API ever disagree, the API is right and the file is stale — tell
> us.** The file is a snapshot taken at a commit; the route is generated per request.

Look there for: every endpoint and payload shape, the `DeviceKey` security scheme, the RFC 7807 error
shape with its `code`/`serverTime` extensions, and the frozen token tables as machine-readable enums
(`TapOutcomeCode`, `ManualOutcomeCode`, `LiveOutcomeCode`).

**[`endpoints.md`](endpoints.md) is a one-page index of every route** — method, path, whether it needs
a device key, and which errors it declares. It is generated from `openapi.json` and CI fails when the
two disagree, so it cannot drift either. It is a **map, not a contract**: it will tell you an endpoint
exists and roughly what it does, and deliberately carries no field-level detail. Generate your client
from `openapi.json`; use the index to find your way around it.

## Connecting to the dev API

The backend runs on a machine on the office LAN. **Ask us for the current IP** — it is DHCP, so it
moves when the router reboots and any number written here would be wrong within a week.

```
http://<dev-host>:5080/api/v1     base path
http://<dev-host>:5080            Swagger UI, browsable in a desktop browser
```

> **Android will block this before it ever reaches us.** Cleartext HTTP is disabled by default from
> API 28, so a request to `http://<dev-host>:5080` fails with a generic network error that looks
> exactly like the server being down. Add a network-security-config exception for the dev host (or set
> `usesCleartextTraffic` on a dev build). This is the most common "your API is broken" report that is
> not the API.

### Get yourself a device key

**Enrol your own — do not wait for us to send you one, and do not commit one anywhere.** Device
enrolment is deliberately open on this network while human authentication is still Phase 6:

```bash
curl -X POST http://<dev-host>:5080/api/v1/devices \
  -H "Content-Type: application/json" \
  -d '{"name":"<your name> - capture app","deviceType":"Mobile","readerModel":"RN/Expo","isActive":true}'
```

The response carries `apiKey` **once and never again** — the server keeps only a hash. Put it straight
into the platform keystore (`expo-secure-store` / Android Keystore), never `AsyncStorage`, and never a
committed file. Lost it? `POST /api/v1/devices/{id}/regenerate-key`.

Enrol your **own** device rather than sharing ours: every tap is attributed to the device that sent
it, so a shared key makes your traffic and ours indistinguishable in the attendance record.

### Test data you can scan

The dev database is seeded with mock serials, shaped like the real thing — ten decimal digits,
**leading zeros significant**:

| Serial | Student | REGNO |
|---|---|---|
| `0012503301` | Maria Santos | `2023-0001` |
| `0012503302` | Juan Dela Cruz | `2023-0002` |
| `0012503303` | Andrea Lim | `2023-0003` |
| `0012503304` | Miguel Gonzales | `2023-0004` |
| `0012503305` | Sofia Ramos | `2023-0005` |
| `0012503306` | Gabriel Flores | `2023-0006` |
| `0001234567` | Isabella Aquino | `2023-0007` |
| `0987654321` | Diego Mendoza | `2023-0008` |

The last two vary the leading-zero count on purpose — if your reader or JSON layer ever coerces a
serial to a number, those are the two that expose it fastest.

**Three useful negative cases**, all of which should fail and are worth asserting against in your own
tests:

| Send this | Expect | Because |
|---|---|---|
| `2023-0001` (the REGNO) | `CardNotFound` | REGNO is not a tap identity |
| `12503301` (zeros stripped) | `CardNotFound` | leading zeros are part of the serial |
| any serial, no `Authorization` | `401 DeviceKeyMissing` | capture endpoints require a device key |

Ask us for an open event's `eventId` — a tap needs one, and `tappedAt` must fall inside that event's
window or you will get `TappedAtOutsideEventWindow` rather than a recorded tap.

---

## Why this file still exists

A schema says what a field *is*. It cannot say what your queue should *do*, and everything below is
the second kind — behaviour we learned the hard way, or decisions whose rationale would look
arbitrary without the story attached. **Keep reading this file; generate your code from the other
one.**

---

## What your queue does with each outcome

The document publishes every token and its HTTP status. It cannot publish this column — whether a
row should be retried, dropped, or treated as poison is the thing you actually branch on, and getting
it wrong is how a queue either loses taps or retries one forever.

**Read the HTTP status first, the `code` second.** Never branch on `title` or `detail`; both are
reworded freely.

**The status for each token is deliberately not repeated here** — see the `TapOutcomeCode` schema,
which generates the pair from the code. A hand-copied table of statuses in this file is exactly the
drift 4e deleted 443 lines to end, and it would be unpinned by any test.

| `code` | Your queue should |
|---|---|
| `Recorded` | **Drop.** Newly stored. |
| `CheckedOut` | **Drop.** The check-out half was newly stored. |
| `DuplicateIgnored` | **Drop.** Already stored under this `deviceTapId` — the retry path working as designed, not an error. |
| `AlreadyRecorded` | **Drop.** Nothing changed. See the reconciliation note below — this one has a catch. |
| `EventNotFound` | **Poison.** Also what a device from another school gets for a foreign event; we never confirm the event exists elsewhere. |
| `CardNotFound` | **Poison — but show it, do not swallow it.** No student holds that UID; retrying will not bind one. Common during rollout while cards are still unbound (see the card-UID rule), so this is the one poison code that needs a real message on screen rather than a silent drop. |
| `DeviceNotRegistered` | **Stop the queue and re-enrol.** A wiped or re-provisioned handset holds a stale device id. Retrying every queued tap forever is exactly the failure this token exists to prevent. |
| `EventNotOpen` | **Poison.** |
| `DeviceMismatch` | **Poison — client bug.** The body's `deviceId` disagrees with the authenticated device. Fix the sender; the row will never succeed as sent. |
| `TappedAtOutOfRange` | **Poison, and alarm.** Your clock is more than 5 minutes fast. Correct it before enqueueing more, or the whole queue is poison. |
| `TappedAtOutsideEventWindow` | **Poison.** The claimed tap time is outside the event window (default 60 min either side, per-school configurable). |
| `DeviceTapIdRequired` | **Poison — client bug.** Missing, or over the 100-character limit. Fix the generator; do not retry. |
| `BatchTooLarge` | **Split and resend.** The only rejection here that is fixable by resending: halve the batch. Cap is 200 rows. |
| `RateLimited` | **Retry, honouring `Retry-After`.** Not an error — back off. |
| `DeviceKeyMissing` / `DeviceKeyMalformed` / `DeviceKeyInvalid` | **Stop.** Do not retry blindly; a wrong secret will not become right. `DeviceKeyInvalid` deliberately does not distinguish an unknown key id from a wrong secret. |
| `DeviceKeyRevoked` / `DeviceInactive` | **Stop and re-enrol.** |
| *(any 5xx)* | **Retry with backoff.** See the known gap below — a single poison row can currently only surface this way. |

### Known gap: there is no row-level server-error token

Every token above describes a *decision* we made about a tap. There is none for "this row threw
unexpectedly", so an unforeseen server-side failure on one row of a batch can only surface as a
**batch-level `5xx`** — which you are told to retry, forever, with nothing naming the poison row.

We froze 4e without closing this because it is your queue's semantics that should decide whether such
a row is retryable or poison, and we did not want to choose for you. **It stays open and it is
additive** — say the word and we add something like `RowFailed` (batch still 200, row `status` 500).

---

## Three rules to implement on your side

### Card UIDs: they are the RFID serial, and they are strings

**The card UID is the physical RFID serial — not the student number.** The two are separate values on
separate columns, and REGNO is **not** a tap identity: authentication on a tap is the scanned RFID
alone.

> **Corrected 2026-07-30.** An earlier revision of this file said a card UID *was* the student number
> and that no binding step was needed. That was wrong. If you built anything on REGNO-as-UID, that is
> the one thing in this document worth re-checking today.

The serial is **decimal digits, no separators**, and in the sample data ten characters wide:

```
0012503326
```

> **Treat it as a string, never as a number, anywhere in your stack.** `0012503326` parsed as an
> integer is `12503326`, which is a different card and will never match. Leading zeros are
> significant. This is the single most likely way for a UID to break silently, and it breaks on your
> side and ours identically.

Normalisation is uppercase with all non-alphanumerics stripped, so `04:A7:B8:C9`, `04-a7-b8-c9` and
`04a7b8c9` are **one card**, stored as `04A7B8C9`. An all-digit serial is unchanged by that rule —
nothing to strip, nothing to uppercase, **leading zeros preserved**. The server normalises inbound, so
you may send the raw reader format; normalise before any *local* cache or comparison, or your dedupe
will disagree with ours.

> **⚠ Expect `CardNotFound` during rollout, and design for it.** The roster the school has given us
> today has **no RFID column** — it is coming in a later export. Until cards are bound, every student
> exists with no card, and every tap against them is `CardNotFound`. Treat that code as an ordinary
> operational state your UI explains ("card not registered yet"), **not** as a defect or a reason to
> drop the tap silently. How cards get bound — bulk from the next roster, or a field binding flow —
> is still open; see the questions at the end.

> **Security note, for awareness rather than code.** A card serial is not a secret: it is a number on
> a card that can be read by anything, and serials in a batch tend to run close together, so a
> plausible neighbouring UID is easy to produce. Device auth, the event window, and audit are the
> mitigations — which is why device keys are not optional.

### `deviceTapId`: always send one, keep it stable, keep it under 100 characters

The idempotency key the whole offline design rests on. Generate it once, when the tap happens on the
device, and **never regenerate it on retry** — regenerating turns a duplicate into a second stored
tap. Required on batch, strongly recommended on single taps.

> **Maximum length: 100 characters** (the column width). A composite like
> `{installId}:{eventId}:{counter}:{uuid}` passes 100 easily. A UUID (36 chars), or a UUID prefixed
> with a short install id, fits comfortably.
>
> **If your scheme cannot fit, tell us before you ship.** Widening the column is a migration, not a
> redesign, and it is far cheaper to do now than after you have queued data in the field.

### Everything is UTC — and your clock is load-bearing

`tappedAt` plus the event's `graceMinutes` is what decides **Present vs Late**. Send UTC with an
explicit `Z`. A bare or offset-bearing local time misjudges the boundary by the server's offset —
eight hours in Manila. This has already been a real defect on our side once.

**We will never silently rewrite your `tappedAt`.** An accepted value is stored exactly as given, and
an unbelievable one is refused rather than clamped — clamping would invent an observation on the
field that decides Present vs Late. Instead:

- Every response carries `serverTime`. Compute an offset and apply it before enqueueing.
- Send `clientClockAt` on each batch — your clock at send time. Its difference from ours is pure skew,
  free of queue latency, and we log and alert on drift.
- The past is never age-limited. An old queued tap is the entire point of the endpoint.

> **Always send `tappedAt` — not only `deviceTapId`.** This is the one recommendation here that exists
> because of a retry consequence rather than a rule.
>
> With `tappedAt: null`, the instant being validated is **re-derived from our clock on every attempt**.
> So a tap that landed at 10:00 whose response you lost, retried at 12:30 after the event's window has
> closed, comes back `TappedAtOutsideEventWindow` — not the `DuplicateIgnored` you would expect for a
> tap that is already recorded. The stored attendance is correct and both codes tell your queue to drop
> the row, so nothing is lost; but reconciliation *by code* is wrong for that row.
>
> Sending an explicit `tappedAt` pins the value across every retry and the problem disappears.
> Omitting it is not a way around the window check either — `null` means "now, on the server", and that
> instant is validated exactly like a value you sent.

---

## Reconciliation has one blind spot

**Any tap that changes nothing returns `AlreadyRecorded` without storing its `deviceTapId`** — so
replaying it returns `AlreadyRecorded` again, never `DuplicateIgnored`.

In `Single` mode — the default — that is the *ordinary* duplicate path, not a rare one: every second
tap of the same student at the same event lands there. Both codes mean "drop it", so your queue
behaves identically either way. It is only reconciliation *by tap id* that cannot see those taps: **if
your client reasons about "did tap X land?" purely from tap ids, treat `AlreadyRecorded` as an answer
it will never get.**

Both halves of a `TimeInOut` pair are independently retryable — send a fresh `deviceTapId` with each
tap and keep it stable across retries of *that* tap.

---

## The event manifest: your offline cache

**`GET /events/{id}/manifest`** — who is expected at an event and which card resolves to whom. Pull it
before you start scanning. The shape is in the generated document; everything below is behaviour it
cannot state.

**It is the invitation, not the roster.** It carries nothing about who has already tapped — that is
`GET /attendance/live/{eventId}`. A manifest carrying attendance state would read as authoritative on
the device, and the one thing this object must never be is authoritative.

### The rule that matters most

**Offline validation against the manifest is display-only and never gating.** A tap whose card UID is
absent from your cached manifest **must still be queued and flushed.** The server rules on it and it
lands as a walk-in (`isExpected: false`).

A client that refuses to capture an unknown card turns "my cache is stale" into "that attendance never
happened", and afterwards the two are indistinguishable. Use the manifest to put a name on screen;
never to decide whether a tap counts.

> **Expect a cardless manifest today.** The roster the school has given us carries no RFID column, so
> `cardUids` is empty for every student right now. A manifest that is entirely cardless should tell the
> operator the offline cache is unusable — not silently offer an index that can never hit.

### Revalidate with `If-None-Match`

Store `version` from the body and send it back as `If-None-Match` on the next pull. Unchanged is a
**304 with no body** — keep what you have.

- **`version` is opaque. Never parse it, never order it, compare it for equality only.**
- The comparison is weak (`W/`) and lenient about quoting, so sending the value back verbatim always
  works.
- A 304 carries the `ETag` too, so a client that lost its stored version can recover it rather than
  re-downloading a body it already holds.
- A 200 is a **wholesale replacement** of your cached copy. This is not a delta and there is no merge
  that is correct.

> **A 304 carries no `serverTime`.** Take your clock offset from the HTTP `Date` header instead.
> Assuming the previous offset still holds because the manifest did not change is wrong — the manifest
> not changing says nothing about the clock.

### Check the clock before you enable scan mode

Compare `serverTime` against the device clock. **More than five minutes apart, do not scan.** Every tap
captured past that threshold arrives as `TappedAtOutOfRange` — poison, dropped by your own queue,
attendance gone. It is the one clock rule that prevents loss rather than reporting it.

Send `?clientClockAt=<your clock, ISO 8601>` on the pull. It is **measured, logged, and can never cause
a refusal** — a value we cannot parse is simply not measured rather than a `400`. Its worth is that it
is the same quantity as flush-time skew, measured hours earlier: a device that pulls at 08:00 and
flushes at 15:00 gives us no drift signal for seven hours, during which every tap it captured is
already unrecoverable.

### What each refusal means

**Two obligations hold across every response here, including the failures.**

- **Never clear the cached manifest on a failure.** A client that wipes on error degrades from
  slightly-stale names to no names, and does it exactly when the network is worst. The only sanctioned
  discard is after the queue drains on a terminal event.
- **No refusal here ever stops tap capture.** Capture and queueing continue whatever this endpoint
  returns. Only the *flush* pauses, and only on the device-key codes below.

| `code` | Your client should |
|---|---|
| `EventNotFound` | **Stop and tell the operator.** Also what you get for an event belonging to another school — we never confirm one exists elsewhere. |
| `EventNotOpen` | **Stop and tell the operator** it has not been opened yet. The event is still `Draft`. |
| `EventFrozen` | **Flush the queue first, then stop.** The event is `Closed` or `Cancelled`. Do not discard the cached manifest until that queue is empty. |
| `ManifestTooLarge` | **Stop, tell the operator, and report it to us.** Deliberately loud rather than truncated — and unlike `BatchTooLarge` there is nothing for you to halve. |
| `RateLimited` | **Retry, honouring `Retry-After`.** Not an error. Keep capturing meanwhile. |
| `DeviceKeyMissing` / `DeviceKeyMalformed` / `DeviceKeyInvalid` | **Stop.** Re-check the stored credential, then re-enrol. Keep the manifest and the queue; pause the flush. |
| `DeviceKeyRevoked` / `DeviceInactive` | **Stop and re-enrol.** Keep the manifest and keep capturing; pause the flush. |

**`EventFrozen` and `ManifestTooLarge` are the only new tokens** — the rest are the ones the capture
path already uses, with the same meanings. As everywhere else: read the HTTP status first and `code`
second, and never branch on `title` or `detail`.

A malformed `{id}` is the ordinary model-validation **400** and carries no `code`. That is a bug on
your side — stop, and do not retry: the same URL is refused forever.

### Never truncated, never paged

Over **20,000 attendees** the endpoint refuses with `ManifestTooLarge` rather than returning a partial
body. A short list is indistinguishable from a small event, and its consequence is legitimately-invited
students showing up on the device as unknown cards — the exact failure this endpoint exists to prevent.
The ceiling sits about two orders of magnitude above the largest event anyone has described; if you
ever see it, something is wrong on our side.

### Budget: 30 pulls per minute, per device

Partitioned by device id and **separate from your tap budget**, so refreshing a cache can never
throttle capture. A device pulls once before a session and then revalidates, so thirty a minute is far
past any honest client and still leaves room for a retry after a network wobble. A refusal costs you
nothing — you keep the manifest you have.

> Note what a 304 does **not** save. It saves your radio, battery and parse; we compose the content
> either way, because the version cannot be known without it. The budget is over pulls, not over bodies.

### Five things about the body that will bite otherwise

- **`attendees` is de-duplicated to exactly one row per student**, however many attached groups reach
  them — key your offline index by `studentId`. Twelve of the fifty-two students in the real roster sit
  in more than one section, so a doubled list would either lose a student's second group or double the
  denominator you show on screen. Both look plausible.
- **`groups` is not `sections`.** A college, a programme and a hand-made "SSC Officers" list are all
  groups. An unrecognised `type` must render as a plain group and **must never be dropped** — otherwise
  a future group kind silently removes students from your filter while they are fully expected on the
  server. An empty `groupIds` is likewise common and not an anomaly: it is the student an organizer
  named by hand. A group filter needs an "all" or "ungrouped" view or those students are invisible on
  the device.
- **`event.endAt` is not the capture window**, and treating it as one loses taps. What we actually
  accept is `startAt`/`endAt` widened by a per-school margin that is deliberately not published —
  publishing it invites you to enforce the window locally and refuse taps we would have accepted. Queue
  everything; let the server rule.
- **An unrecognised `attendanceMode` must be treated as `Single`.** A device that refuses to scan
  because a future mode was added is worse than one that captures single taps.
- **`studentNumber` is not a tap identity** and must never be matched against a scanned serial. It and
  the card UID come from different source columns with no relationship between them; a device that
  falls back to matching the number when a UID misses will attribute a tap to a stranger.

**Event-scoped, not occurrence-scoped.** One manifest serves every occurrence of a recurring event, and
`startAt`/`endAt` are the template's. Recurrence is unbuilt; an `occurrenceId` parameter is additive
when it lands.

---

## Live attendance: polling, not SignalR

**Decision taken 2026-07-29.** The plan named a SignalR hub; we ship a cursor-delta polling endpoint
instead. The shape is in the document — the reasoning is here, so it does not look arbitrary:

A SignalR client must fetch a snapshot on every reconnect to close the gap it missed, so this endpoint
is required under *both* designs. Polling is also far kinder to a campus firewall, needs no backplane
or sticky sessions on our side, and does not force your device key into a WebSocket query string where
it lands in access logs. **If a hub is ever added it will emit exactly the same delta object, so your
reducer would not change.**

Three behaviours the schema cannot state:

- **`hasMore` is the one field you must not ignore.** A response carries at most 500 rows. When it is
  `true`, the page was truncated and its `cursor` points at the last row you received — **poll again
  immediately rather than waiting `pollAfterSeconds`.** Otherwise a 5,000-attendee event takes ten
  polls at five seconds each to fill a dashboard, and it looks like the feed is broken rather than
  paging.
- **Respect `pollAfterSeconds`** (default 5, server-configurable 1–300). It is how we back clients off
  under load without you shipping a release.
- **On `InvalidCursor`, re-poll with no `since`** and take a fresh snapshot. We validate the cursor's
  *shape*, not that we issued it: a well-formed cursor from elsewhere decodes, and one pointing beyond
  our current position is treated as a fresh snapshot rather than an error. Send back the cursor from a
  previous response verbatim and none of this concerns you. (Cursors are 11-character base64url, no
  padding, safe to concatenate by hand. An earlier build issued 12-character standard base64; those are
  still accepted, so a client mid-upgrade is not stranded.)

Capture and polling rate limits are partitioned separately, so a browser tab polling a dashboard can
never spend a kiosk's tap budget.

---

## What we still need from you

Answers change our schema, not just our docs — the first two are the ones that get expensive after you
ship.

1. **`deviceTapId` generation** — what it derives from, and its uniqueness guarantee. **Urgent:** see
   the 100-character limit. If your scheme does not fit, we widen the column before you ship, not
   after.
2. **A row-level server-error token** — do you want it, and would your queue treat it as retryable or
   as poison? See the known gap above. Your queue's semantics should decide, not ours.
3. **Card binding — do you want a flow for it in the app?** Cards are not bound yet and the roster we
   have carries no RFID column. If binding happens entirely from the next roster export, you need
   nothing. If the school wants a card registered *in the field* — scan an unknown card, attach it to a
   student — that is a screen in your app and an endpoint on ours, and neither exists today. **Tell us
   which, because it is the one open question that could add scope to your side.**
4. **Offline queue semantics** — retry/backoff policy, how long a tap may sit queued, what you do with
   a permanently rejected row.
5. **Batch size** — does 200 rows suit your flush strategy? The cap is enforced, not provisional.
6. **Device enrolment** — is QR-scan-at-issue the UX you want? What should happen on reinstall?
7. **Clock skew** — how do you correct or flag a drifted device clock?
8. **Manifest refresh cadence** — when do you re-pull, and does 30 per minute per device leave you
   enough headroom? We sized it for "once before a session, then revalidate". If your design refreshes
   on a timer while scanning, or re-pulls on every reconnect, say so — the budget is a constant we can
   raise, and finding out from a `RateLimited` in the field is the expensive way.
9. **Did anything you had already built depend on tap *failures* returning `TapResult` rather than a
   problem body?** That changed in Phase 4c and you were the only consumer we could not check. If it
   broke something, tell us — `success: false` bodies are gone from the `4xx` responses.

> `TapResult.success` still exists on **200** bodies and is marked deprecated in the schema. It is
> redundant with `code` and will be removed in a later contract revision — branch on `code`. We kept it
> rather than making a second breaking change to that body while question 7 is unanswered.

---

## Not built yet

| Item | Status |
|---|---|
| Authentication for human users (JWT + RBAC) | Phase 6 — every non-capture endpoint is open until then |
| Reports | Phase 5 |
| Card binding — bulk or in-the-field | Undecided; see question 3. No endpoint exists either way |

Until Phase 6 lands, this API must stay on a local or trusted network.

> **The admin SPA has since been built** and is wired to this API rather than to mock data — students,
> events and their audiences, devices, and the SIS roster import. It is listed here only because an
> earlier revision of this file said it was not: nothing about it changes anything on your side.

---

## How this document changes

The generated document is the contract and moves with the code. This file changes only when
*behaviour* changes — a new queue action, a new footgun, a decision that needs its reasoning recorded.
If the two ever disagree about a shape, **the generated document is right and this file has a bug**;
tell us and we will fix it.

Questions to the backend team. Quote the `traceId` from any error body you want us to look into.
