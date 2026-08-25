# EAMS Testing Guide (developer)

How to run this system locally, exercise it by hand, and run the checks that gate a change.

**Audience: developers.** Testers working against a deployed instance want
[`QA-OVERVIEW.md`](QA-OVERVIEW.md) instead — it assumes a URL and an APK, and no toolchain at all.
The two documents are deliberately disjoint: everything requiring a command line lives here.

| | |
|---|---|
| Branch | `feat/backend-phases-0-3a` |
| Last verified | 2026-08-24 |
| Spec of record | `../Events-Attendance-Monitoring-System-Technical-Plan.md` |
| Drift register | [`adr/ADR-001`](adr/ADR-001-schema-drift-from-technical-plan.md) (+ ADR-002, ADR-003) |
| Deployment | [`DEPLOY-IIS.md`](DEPLOY-IIS.md) |
| Mobile contract | [`api/README-for-mobile-developer.md`](api/README-for-mobile-developer.md) |

---

## 1. Running it

**Prerequisites:** .NET 9 SDK, Node 22, SQL Server reachable (LocalDB / SQLEXPRESS / container), and
**Docker running** if you intend to run the integration tests.

```bash
# Backend -> http://localhost:5080  (Swagger UI at the root)
cd backend/EAMS.Api && dotnet run --urls "http://localhost:5080"

# Web admin -> http://localhost:5173
cd web-admin && npm install && npm run dev
```

Start the backend first. The SPA has real loading / empty / error / retry states, so against a dead
API it renders error panels — that is the designed behaviour, not a failure to diagnose.

The SPA calls `http://localhost:5080/api/v1` by default. Override in `web-admin/.env.local` via
`VITE_API_BASE_URL` (see `.env.example`). **Vite inlines it at build time**, so a change needs a
dev-server restart, and a *built* bundle cannot be repointed — see `DEPLOY-IIS.md` §5.

### Database

Migrations run on startup and are idempotent, as is seeding. **Data survives restarts.** The
connection string's login needs rights to create `EAMS`, or create it empty and grant `db_owner`.

Adding a migration — scaffold from Infrastructure alone, which owns the design-time factory:

```bash
dotnet ef migrations add <Name> --project backend/EAMS.Infrastructure
```

---

## 2. `ASPNETCORE_ENVIRONMENT` decides three things at once

This is the setting that most often explains "it works on my machine".

| | `Development` | `Production` |
|---|---|---|
| Swagger UI at the API root | yes | **no** |
| Seeding (school, term, students, kiosk device) | yes | **no** |
| Well-known development kiosk key | seeded | **not seeded** |
| "Simulate RFID tap" panel in the SPA | present | **stripped from the bundle** |

**On `Production` you get a migrated but empty database.** That is the intended state, not a broken
install. First-run order is then: create a `Schools` row (the tenancy filter hides *everything*
without one, the Terms page included) → create a term at `/terms` → import the roster.

> **Never hand-write `INSERT INTO dbo.Terms`.** The `/terms` page enforces rules SQL does not — code
> length, refusal of leading/trailing whitespace, and moving the current-term flag in the single
> statement the filtered unique index tolerates. Dropping the dev database and re-seeding is fine.

### The all-or-nothing seed

The bulk seed returns early if a `School` row already exists. **A database created before a given
seed row was added never gains it.** A dev carrying an older `EAMS` database therefore has no term —
and with no term the roster-import page has an empty picker and refuses to stage a batch, because the
importer takes `TermId` as input. Create one at `/terms`, or drop the database.

Seeding a term is the one deliberate exception to the early return, so a carried-over database still
gets one on a fresh run.

---

## 3. Seed fixture (Development only)

**School:** University of San Agustin (`USA`), `Asia/Manila`. **Term:** `2025-2026-1`.
**Device:** "Development Kiosk". **Events:** one Open (started 30 min ago, 2 taps recorded), one Closed.

**8 students.** `StudentNumber` is the REGNO; `CardUid` is the RFID serial. **Different values for
different things** (client correction 2026-07-30, register D-43).

| REGNO | Name | Course / Year / Section | Card UID |
|---|---|---|---|
| 2023-0001 | Maria Reyes Santos | BSIT / 3rd Year / A | `0012503301` |
| 2023-0002 | Juan Cruz Dela Cruz | BSIT / 3rd Year / A | `0012503302` |
| 2023-0003 | Andrea Lim | BSCS / 2nd Year / B | `0012503303` |
| 2023-0004 | Miguel Tan Gonzales | BSCS / 2nd Year / B | `0012503304` |
| 2023-0005 | Sofia Villa Ramos | BSIT / 1st Year / C | `0012503305` |
| 2023-0006 | Gabriel Flores | BSA / 4th Year / A | `0012503306` |
| 2023-0007 | Isabella Marie Aquino | BSN / 2nd Year / A | `0001234567` |
| 2023-0008 | Diego Luis Mendoza | BSIT / 3rd Year / A | `0987654321` |

> The serials are **mock** — the client's roster export carries no RFID column yet. They are shaped
> like the real sample rather than invented freely, because the shape is what breaks: ten decimal
> digits, no separators, **leading zeros that matter**. Two rows vary the zero count on purpose, so a
> serializer that only ever sees the common `00` shape cannot pass by luck. Anything that parses one
> as a number turns `0012503301` into `12503301` and the card stops resolving.

---

## 4. Auth: what is and is not enforced

**Human authentication is not built** — JWT + permission-based RBAC is Technical Plan §11, deferred
by ADR-001 D-6/D-28. Every non-capture endpoint is open. The assembly carries
`[assembly: AuthorizationNotEnforced]` and the API logs the fact on every startup, so this is
asserted rather than merely true. `AuthorizationSeamTests` and `PermissionRegistryTests` hold the
seam. Do not "fix" it incidentally.

**Device-key auth on the five capture routes is real and enforced:**

```
Authorization: DeviceKey eams_dk_<keyId>_<secret>
```

Token shape: `eams_dk_` + 12-char key id + `_` + 64-char hex secret. **Only the hash is stored** — a
lost key is regenerated, never recovered. Refusals distinguish missing / malformed / invalid /
revoked / inactive, and a device from another school answers `DeviceNotRegistered` — deliberately
indistinguishable from an unknown device.

---

## 5. Exercising the system by hand

### 5.1 In the SPA (`http://localhost:5173`)

`/` dashboard · `/students` · `/students/import` · `/events` · `/events/:id` · `/devices` · `/terms`

For step-by-step flows with expected outcomes, use [`QA-OVERVIEW.md`](QA-OVERVIEW.md) Parts 2–5 —
they apply unchanged to a local instance. Do not duplicate them here.

### 5.2 Simulate a tap without a handset — dev builds only

`/events/:id` carries a **"Simulate RFID tap"** panel behind `import.meta.env.DEV`. Paste a device
key token into it and the browser records real attendance as that device for as long as the tab is
open (held in `sessionStorage`, never persisted, never bundled).

**Register a device dedicated to simulation** rather than reusing a real reader's key — every tap is
attributed to the device that sent it, so a shared key makes the two indistinguishable in the record.

> `import.meta.env.DEV` is a compile-time literal, so the production build drops the element, the
> import, the module and `deviceKey.ts` with it. That is verified against the real artefact:
>
> ```bash
> cd web-admin && npm run build && grep -c "eams_dk_\|DeviceKey\|deviceKey" dist/assets/*.js   # -> 0
> ```
>
> **Deployed testers therefore have no simulate-tap button.** Their taps must come from the handset.

### 5.3 Against the API directly

Swagger at the API root in Development, or curl / Postman. Capture routes need the header from §4.

```bash
# Enrol a device — the bootstrap, deliberately open while §11 is unbuilt. Returns apiKey ONCE.
curl -X POST http://localhost:5080/api/v1/devices \
  -H "Content-Type: application/json" \
  -d '{"name":"QA sim","deviceType":"Mobile","readerModel":"curl","isActive":true}'

# One tap.
curl -X POST http://localhost:5080/api/v1/attendance/tap \
  -H "Authorization: DeviceKey eams_dk_..." \
  -H "Content-Type: application/json" \
  -d '{"eventId":"<guid>","cardUid":"0012503301","tappedAt":"2026-08-24T02:00:00Z","deviceTapId":"qa-001"}'
```

**Send `tappedAt` explicitly, in UTC with a `Z`.** It plus the event's grace decides Present vs Late,
and it is validated against server time and the event window.

**Replay the same `deviceTapId` from the same device** — it must return the record it already
produced, not a second row. The key is the *pair* `(DeviceId, DeviceTapId)`, matching
`UX_Attendance_Device_DeviceTapId`. This is the foundation of offline mobile sync (plan §8.2);
**never make capture non-idempotent.**

### 5.4 Published ceilings worth probing

| Ceiling | Value | Refusal |
|---|---|---|
| Tap batch rows | **200** | `BatchTooLarge`, whole batch refused, number echoed for chunking |
| Tap batch body | **256 KB** | A separate guard — neither replaces the other |
| `deviceTapId` | **100** chars | `DeviceTapIdRequired`, nothing recorded |
| Audience filter rows | **20** | `TooManyFilterRows` — **checked before field validation** |
| Audience resolved students | **5000** | Refused loudly, never truncated |
| Live feed page | **500** rows | Cursor-paged |

> The audience ordering is pinned by test (`141780a`): a set that is both over the ceiling *and* names
> an unregistered field answers `TooManyFilterRows`. Swapping the checks changes the `code` on the
> wire, which is why a test holds it.

---

## 6. Automated tests

### 6.1 What CI runs — run these before declaring work done

```bash
dotnet build EAMS.sln -c Release
dotnet test  EAMS.sln -c Release
cd web-admin && npm run lint      # oxlint
cd web-admin && npm run build     # tsc -b && vite build
```

CI additionally regenerates the API endpoint index and fails on drift:

```bash
cd web-admin && node ../scripts/generate-endpoint-index.mjs
```

> **Gap worth knowing:** `web-admin` has a vitest suite (`web-admin/test/`, 10 files covering the
> draft/validation logic the SPA copies from the server) and **CI does not run it.** The Web Admin job
> runs lint, build, and the endpoint-index check only. Run it by hand:
>
> ```bash
> cd web-admin && npm test        # vitest run
> ```
>
> Wiring it into `ci.yml` is a one-line change nobody has made yet.

### 6.2 Layout

`backend/EAMS.Tests` splits by what a test needs:

| Folder | Files | Needs |
|---|---|---|
| `Unit/` | 27 | Pure logic — no database |
| `Integration/` | 53 | A real SQL Server |

Current totals move every commit, so this document does not pin one. To count:

```bash
dotnet test EAMS.sln -c Release --list-tests | wc -l
```

### 6.3 Integration tests need Docker

They start **SQL Server 2022 via Testcontainers**, create and drop their own `EAMS_Test_<guid>`
database, and **never touch the dev `EAMS` one**. With Docker stopped they fall back to
`.\SQLEXPRESS` and print that they did — loudly, and never in CI.

> A `dotnet test` failure naming a container or a connection string is an environment problem, not a
> product defect. Read the console banner before diagnosing.

**Do not move the SQL-Server-dependent tests to EF InMemory.** Filtered unique indexes, SQL Server's
NULL-equality semantics inside a unique index, and unique-violation races all vanish there — and those
are precisely what the suite exists to protect. This has bitten before: EF silently filters a unique
index over a nullable column, which nearly left attendance unconstrained.

---

## 7. Repo rules that shape how tests are written

Carried here because they are testing rules, not architecture rules.

- **Verify schema against the database, not the model.** An EF model that looks constrained can map
  to an index that constrains nothing. Assert against the created database.
- **Negative-control every fix.** Revert the fix, watch the test fail, and check it fails *for the
  right reason*. This has caught a green-but-useless concurrency test. Undo the control with `Edit`,
  **never `git checkout --`** — that restores to HEAD and silently discards uncommitted work.
- **Verify a claimed defect against the stack before "fixing" it.** EF pre-escapes `%` and `_` in
  `.Contains(search)`; a "wildcard hole" reported in three services was not one, and the fix would
  have broken working code.
- **Normalize first, compare second.** Any new UID-handling test asserts against the normalized form.
- **Boundaries only.** Validate at API / user input / external calls; trust internal code.

---

## 8. Troubleshooting

| Symptom | Cause |
|---|---|
| SPA loads, every panel errors | Backend not running, or `VITE_API_BASE_URL` wrong. Restart the dev server after changing it |
| Term dropdown empty on the import page | No term — §2. Create one at `/terms` |
| Terms page itself will not load | No `Schools` row; the tenancy filter hides everything |
| No Swagger at the API root | `ASPNETCORE_ENVIRONMENT` is not `Development` |
| No simulate-tap panel | Same — or you are looking at a production build, where it is stripped |
| Every tap answers `CardNotFound` | The card is unbound or revoked. Bind one via `POST /students/{id}/cards` |
| Tap answers `DeviceNotRegistered` | Key missing, revoked, or belonging to another school |
| Tap answers `EventNotOpen` | Event is Draft, Closed or Cancelled |
| `dotnet test` fails on a container | Docker is not running — §6.3 |
| CS0122 reaching for `EamsDbContext` | By design. Controllers speak DTOs to the `EAMS.Application.Abstractions` services |
