# EAMS API — start here (mobile / APK developer)

> **What changed recently:** [`mobile-changes.md`](mobile-changes.md). Read the top entry before
> a release - it says whether anything needs to change on your side, and today the answer is no.

The backend is ready for a capture app. This page is a **cover sheet**: what to read, in what
order, and what to ask us for. It restates nothing — every shape and rule lives in one of the
three files below, and duplicating them here is how a handoff goes stale.

## Read these three, in this order

| # | File | What it is |
|---|---|---|
| 1 | [`attendance-contract-handoff.md`](attendance-contract-handoff.md) | **The one to actually read.** Written for you. How to enrol a device, test card serials, what your queue does with each outcome code, the clock and card-UID rules, manifest caching. |
| 2 | [`openapi.json`](openapi.json) | **The contract.** Generate your client from this. Every endpoint, payload, error shape and enum. Exported from a running build, so it cannot disagree with the API. |
| 3 | [`endpoints.md`](endpoints.md) | A one-page index of all 44 routes — a map for finding your way around #2. No field-level detail by design. |

If #2 and the running API ever disagree, the API is right and the file is stale — tell us.

## The endpoints your app uses

All under `/api/v1`. Everything below except enrolment requires a device key.

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/devices` | Enrol this device, receive `apiKey` **once**. No key needed — this is the bootstrap. |
| `GET` | `/events/{id}/manifest` | Offline cache: who is expected, which card resolves to whom. Supports `If-None-Match`. |
| `GET` | `/students/by-card/{cardUid}` | Live UID → student, for the scan screen. |
| `POST` | `/attendance/tap` | One tap. |
| `POST` | `/attendance/tap/batch` | Offline queue flush. Max 200 rows. |
| `POST` | `/devices/{id}/heartbeat` | Liveness. |

Not yours, but useful: `GET /attendance/live/{eventId}` is the cursor-delta feed a dashboard
polls. There is no SignalR hub — see the handoff for why.

## The five things that bite hardest

Each is explained properly in the handoff; this is the index so you know what to look for.

1. **Card UID is the RFID serial, not the student number** — and it is a **string**. `0012503301`
   parsed as a number is a different card. Leading zeros are significant.
2. **`deviceTapId` is generated once per tap and never regenerated on retry.** Max 100 characters.
   If your scheme does not fit, tell us before you ship — widening the column is cheap now.
3. **Send UTC with an explicit `Z`, and send `tappedAt` explicitly** rather than leaving it null.
   `tappedAt` plus the event's grace decides Present vs Late.
4. **Branch on HTTP status first, then `code`.** Never on `title` or `detail` — both are reworded
   freely. The handoff has the retry/drop/poison action for every token.
5. **The manifest is display-only, never gating.** A card missing from your cached manifest still
   gets queued and flushed. The server rules on it.

## Two Android-specific gotchas

- **Cleartext HTTP is blocked from API 28.** The dev backend is plain `http://` on the LAN, so a
  request fails with a generic network error that looks exactly like the server being down. Add a
  network-security-config exception for the dev host, or set `usesCleartextTraffic` on a dev build.
  This is our most common false "your API is broken" report.
- **Store the device key in Android Keystore / `expo-secure-store`.** Never `AsyncStorage`, never a
  committed file. The server keeps only a hash and cannot re-issue it — lost means
  `POST /devices/{id}/regenerate-key`.

## What to ask us for

- **The dev host IP.** It is DHCP and moves, so it is deliberately not written down here.
- **An open event's `eventId`.** A tap needs one, and `tappedAt` must fall inside its window.

Enrol your **own** device rather than sharing ours — every tap is attributed to the device that
sent it, so a shared key makes your traffic and ours indistinguishable in the record.

## What is not built

| Item | Status |
|---|---|
| Human authentication (JWT + RBAC) | Phase 6. Every non-capture endpoint is open until then — keep this API on a trusted network. |
| Card binding (bulk or in-the-field) | **Decided — bulk, from the roster export. No scope on your side.** See below. |
| Reports | Phase 5 |

## Open questions we need answered

The handoff lists nine. Card binding — previously the one that could have added scope to your side
— **is now closed**, and these two remain.

1. **`deviceTapId`** — what does it derive from, and does it fit 100 characters?
2. **Manifest refresh cadence** — the budget is 30 pulls per minute per device, sized for "once
   before a session, then revalidate". If you refresh on a timer while scanning, say so.

### Closed: card binding — **you build nothing**

Binding happens **in bulk, from the roster export** ([Phase 5 §6](../PHASE-5-YEAR-LEVEL-AND-TERM-ADMIN.md),
D-51). There is no in-field registration screen and no endpoint for one. Your app scans a card and
taps; it never enrols one.

Two things this does not change:

- **Every tap is still `CardNotFound` until that export lands.** Build and test against the outcome
  code, not against a populated database. `CardNotFound` is a normal, permanent outcome for an
  unbound card — not a transient error to retry.
- **The manifest is still display-only, never gating.** An unknown card is queued and flushed
  regardless of what your cached `cardUids` contain. The server rules.

Questions to the backend team. Quote the `traceId` from any error body you want us to look into.
