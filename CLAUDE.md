# EAMS — Events Attendance Monitoring System

Attendance monitoring for students with RFID integration (University of San Agustin / CICSS).
Repo: `Karl0308/EventAttendance` · working copy: `D:\Personal Apps\USA-Attendance`

**Status: early mock build.** Backend and frontend both run, but on *separate* mock data —
they are not wired together yet. Treat anything outside the core slice as unbuilt, not broken.

## Layout

| Path | What | Stack |
|---|---|---|
| `backend/` | REST API — students, events, RFID tap capture. Split per plan §3: `EAMS.Api` → `EAMS.Application` → `EAMS.Domain`; `EAMS.Infrastructure` owns EF | .NET 9 Web API, EF Core 9 + **SQL Server**, Swashbuckle 7 |
| `docs/adr/` | Architecture Decision Records. ADR-001 is the drift register against the Technical Plan | |
| `web-admin/` | Admin SPA — dashboard, students, events, live attendance | React 19, TS ~6.0, Vite 8, MUI 6 + X-Data-Grid 7, react-router 7, oxlint |
| `.github/workflows/ci.yml` | CI + GitHub Pages deploy of `web-admin` on push to `main` | |
| `Events-Attendance-Monitoring-System-Technical-Plan.md` | **The spec.** Cite section numbers (§4 schema, §6 API, §11 RBAC, §15 roadmap) | |

`EAMS.sln` at the root is what CI restores/builds/tests.

## Run

```bash
# one-time, per machine: the host refuses to start without a signing key
cd backend/EAMS.Api && dotnet user-secrets set "Jwt:SigningKey" "<64+ random characters>"

# optional, Development only: seeds dev-admin@usa.edu.ph. Omit and the seed is skipped, loudly.
cd backend/EAMS.Api && dotnet user-secrets set "Seed:DevelopmentSuperAdminPassword" "<something long>"

# backend → http://localhost:5080 (Swagger at root)
cd backend/EAMS.Api && dotnet run --urls "http://localhost:5080"

# web admin → http://localhost:5173
cd web-admin && npm install && npm run dev
```

`Jwt:SigningKey` is **not optional and has no development default**. A generated-on-startup key would
rotate every restart and invalidate every live session silently, so the host throws instead — on a
missing key, one under 32 bytes (HMAC-SHA256's floor, RFC 7518 §3.2), or one that looks copied from a
sample. On IIS it is the `Jwt__SigningKey` environment variable on the application pool.

Checks CI runs (run these before declaring work done):
`dotnet build EAMS.sln -c Release` · `dotnet test EAMS.sln -c Release` · `npm run lint` · `npm run build`

## Project-specific rules

- **The Technical Plan is the source of truth for schema and API shape.** Before adding an
  entity, endpoint, or column, check whether the plan already defines it — match its names and
  types rather than inventing new ones. If you deviate, say so explicitly (ADR drift).
- **`web-admin/src/api.ts` is the single seam to the backend.** It is a mock facade whose method
  shapes mirror the real API and are all `async`. Wiring the SPA to .NET means replacing bodies
  in that file with `fetch` — nothing in components should reach around it.
- **Card UIDs are normalized** (uppercase, separators stripped) on both sides. Any new
  UID-handling code normalizes first, compares second.
- **The tap flow is idempotent by `deviceTapId`.** That key is the foundation of the planned
  offline mobile sync (plan §8.2) — never drop it or make capture non-idempotent.
- **Persistence is SQL Server, migrations-first.** `Migrations/Section4Baseline` is the Technical
  Plan §4 schema, authored to stay verifiable against the document; the academic layer lands as an
  additive migration on top of it. Schema changes need a migration in the same change — and never
  data-loss SQL (global hard rule). Scaffold from Infrastructure alone (a design-time factory lives
  there): `dotnet ef migrations add <Name> --project backend/EAMS.Infrastructure`.
- **The app migrates and seeds on startup**, both idempotent; seeding is skipped in Production.
  Data now survives restarts — no more clean slate every run. The seed is **all-or-nothing**: it
  returns early if a `School` row exists, so a database created before a given seed row was added
  never gains it. A dev carrying over an older `EAMS` database therefore has no `Term` — and with no
  term the roster-import page has an empty picker and refuses to stage a batch, because the importer
  takes `TermId` as input.
- **Terms are created in-product** — the SPA's `/terms` page over `POST /api/v1/academic/terms`,
  `PUT /api/v1/academic/terms/{id}` and `PATCH /api/v1/academic/terms/{id}/current` (D-53, the write
  surface `docs/PHASE-5-YEAR-LEVEL-AND-TERM-ADMIN.md` D-50 planned). That is the fix for the missing-term
  case above, and it is the only supported one: **do not write `INSERT INTO dbo.Terms` by hand**, in a
  doc or a script or a session. The route enforces the code rules (`TermText` — length, and leading or
  trailing whitespace refused rather than trimmed) and moves the current-term flag in the one statement
  `UX_Terms_SchoolId_Current` tolerates. Dropping the database and letting it re-seed is still fine on a
  dev machine. `AcademicController` stays read-only; these writes live on `TermAdminService` /
  `ITermAdminService`.
- **Nothing outside `EAMS.Infrastructure` can see `EamsDbContext`.** Every type in that assembly is
  `internal` except `AddEamsInfrastructure`. Controllers talk to `IStudentService` /
  `IEventService` / `IAttendanceService` from `EAMS.Application.Abstractions` and speak DTOs only.
  Reaching for the DbContext from a controller is a compile error (CS0122), by design.
- **Auth is deliberately stubbed** — endpoints are open. Don't "fix" this incidentally; JWT +
  permission-based RBAC is a planned phase (plan §11).
- **`backend/EAMS.Tests` is the test project**, split into `Unit/` (pure logic, no database) and
  `Integration/` (real SQL Server). The integration half starts SQL Server 2022 via Testcontainers,
  so Docker must be running locally; with Docker stopped it falls back to `.\SQLEXPRESS` and says
  so on the console, but never in CI. It creates and drops its own `EAMS_Test_<guid>` database and
  never touches the dev `EAMS` one. **Do not move the SQL-Server-dependent tests to EF InMemory** —
  filtered unique indexes, SQL Server's NULL-equality inside a unique index, and unique-violation
  races all vanish there, and those are precisely what the suite exists to protect.
- **`backend/Directory.Build.props` owns `TargetFramework` / `Nullable` / `ImplicitUsings`** for
  every backend project. Don't re-declare them in a `.csproj`.
- Entity string fields carry enum-ish values as `string` (`Status`, `AttendanceMode`,
  `CaptureMethod`). If these become real enums, that is a schema migration — treat it under the
  global no-DROP/no-rename-without-data-migration rule.
- `web-admin` deep links rely on the CI `404.html` copy for the GitHub Pages SPA fallback. Don't
  remove that step; Pages has no server rewrite.

## Team X

Global agents apply (see `~/.claude/CLAUDE.md`). Fit for this repo:

- **ray-backend** — .NET controllers, EF Core, real persistence, JWT/RBAC, SIS import
- **vaness-frontend** — React 19 + MUI SPA, DataGrid, a11y, live dashboard
- **alhassad-benjamini** — owns standing up `EAMS.Tests`, tap-flow edge cases, e2e
- **jose-arch** — deployment split (SaaS vs on-prem, plan §13), SignalR vs polling, mobile sync design
- **jteamleader** — the plan's roadmap (§15) is multi-phase by construction
- **jj-reviewer** — gates every non-trivial change

Memory lives in `C:\Users\Acer\.claude\projects\D--Personal-Apps-USA-Attendance\memory\`.
