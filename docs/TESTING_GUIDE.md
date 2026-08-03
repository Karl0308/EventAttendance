# EAMS Testing Guide (local, informal — not a durable doc)

Snapshot as of 2026-07-31, branch `feat/backend-phases-0-3a`, 29 commits ahead of `main`, unpushed.
This is a working notes file for manual testing, not an ADR or spec — treat it as disposable.

## 1. Progress summary

Backend and web-admin are now **wired together** (not separate mocks anymore, per commit `6f4edbe`).
The API runs on **EF Core InMemory**, reseeded fresh every process start — nothing you enter survives
a backend restart.

| Area | Status |
|---|---|
| Students — list, add, edit, delete, issue RFID card | Done (`aaa9dfa`) |
| Events — create, edit, delete, open/close/cancel | Done (`178e3cd`, `7871639`, `7de87ba`) |
| Academic reference reads (courses/sections) | Done (Phase 3b-2) |
| Pagination + named permission codes on the 9 admin list reads | Done (`c9d3554`) |
| RFID tap capture (device-authenticated) | Done, backend-only — no UI for it |
| Live attendance dashboard, reports, SIS import, recurring schedules | **Not started** |
| Auth / login / RBAC enforcement | **Not started — deliberately stubbed** |

## 2. Running it

**Backend** (SQL Server-backed now per project CLAUDE.md, but seeded fresh each start in Development):

```bash
cd backend/EAMS.Api
dotnet run --urls "http://localhost:5080"
```
Swagger UI: http://localhost:5080/ (root)

**Web admin**:

```bash
cd web-admin
npm install
npm run dev
```
Runs at http://localhost:5173, points at `http://localhost:5080/api/v1` by default
(override via `web-admin/.env.local` → `VITE_API_BASE_URL`, see `.env.example`).

Start the backend first — the SPA has real loading/error/retry states now, but there's nothing to
show until the API answers.

## 3. Login — there isn't one

**No username/password.** Auth is deliberately stubbed (project rule, plan §11 not built yet). Every
endpoint is open; the SPA has no login route (`web-admin/src/App.tsx` goes straight to `/`,
`/students`, `/events`, `/events/:id`). Just open http://localhost:5173.

The one credential that exists is **not for the web admin** — it's a seeded RFID kiosk device key for
testing the tap-capture API directly (Swagger/Postman), from
`backend/EAMS.Infrastructure/Data/SeedData.cs`:

```
Device name: Development Kiosk
API key:     eams_dk_0de0de0de0de_0de00de00de00de00de00de00de00de00de00de00de00de00de00de00de00de0
```
Only ever seeded when `ASPNETCORE_ENVIRONMENT=Development` (absent in Staging/Production).

## 4. Seed data (fresh on every backend restart)

**School:** University of San Agustin (code `USA`)

**8 students** (`StudentNumber` = REGNO, separate from the RFID `CardUid` — don't confuse them):

| REGNO | Name | Course/Year/Section | Card UID (RFID) |
|---|---|---|---|
| 2023-0001 | Maria Reyes Santos | BSIT 3rd Year A | 0012503301 |
| 2023-0002 | Juan Cruz Dela Cruz | BSIT 3rd Year A | 0012503302 |
| 2023-0003 | Andrea Lim | BSCS 2nd Year B | 0012503303 |
| 2023-0004 | Miguel Tan Gonzales | BSCS 2nd Year B | 0012503304 |
| 2023-0005 | Sofia Villa Ramos | BSIT 1st Year C | 0012503305 |
| 2023-0006 | Gabriel Flores | BSA 4th Year A | 0012503306 |
| 2023-0007 | Isabella Marie Aquino | BSN 2nd Year A | 0001234567 |
| 2023-0008 | Diego Luis Mendoza | BSIT 3rd Year A | 0987654321 |

Card UIDs are 10-digit decimal strings — **leading zeros matter**, don't paste them into Excel/a
numeric field or they'll get truncated.

**2 events:**
- "University Convocation 2026" — **Open**, started 30 min ago, 2 taps already recorded (Maria =
  Present, Juan = Late)
- "IT Week Seminar" — **Closed**, 3 days ago

## 5. What you can test in the web admin (http://localhost:5173)

- **Dashboard** — overview of the seeded data
- **Students page** — list (paginated), add a new student, edit one, delete one, issue an RFID card
  to a student who doesn't have one
- **Events page** — list, create a new event, edit one, delete one
- **Event detail page** (`/events/:id`) — open the Convocation event, try **open → close → cancel**
  transitions, view the 2 seeded attendance records

Anything you add (students, events) lives only in memory — restarting `dotnet run` wipes it back to
the 8 students / 2 events above.

## 6. Testing the tap-capture API directly (no UI for this yet)

Use Swagger at http://localhost:5080/ or curl/Postman. The manual/tap endpoints need the device API
key above as a bearer/header credential (check Swagger's schema for the exact header name — it's
under the Devices/Attendance sections). Idempotency key is `deviceTapId` — same value twice must not
double-record.

## 7. Automated tests

```bash
dotnet build EAMS.sln -c Release
dotnet test  EAMS.sln -c Release      # 1106 tests as of the last count (2026-07-30)
cd web-admin && npm run lint          # oxlint
cd web-admin && npm run build         # tsc -b && vite build
```
Integration tests want Docker running (Testcontainers → SQL Server 2022); without Docker they fall
back to `.\SQLEXPRESS` and print that they did — never silently in CI.
