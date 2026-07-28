# EAMS Backend — Core Slice

A runnable .NET 9 Web API implementing the **core slice** of the
[Events Attendance Monitoring System Technical Plan](../Events-Attendance-Monitoring-System-Technical-Plan.md):
Schools, Students, RFID Cards, Events, and the attendance **tap** capture flow.

## What's included

- **SQL Server + EF Core 9, migrations-first.** `Migrations/Section4Baseline` is the plan's §4
  schema. The app migrates on startup, and seeds dev convenience data outside Production — both
  idempotent, so data survives restarts.
- **Open/stubbed auth** — all endpoints are reachable without logging in (JWT/RBAC is plan §11,
  deferred). `[HasPermissionNotEnforced]` marks the seam; it enforces nothing, deliberately.
- **A `SchoolId` global query filter** that is *active*, pinned at startup to the single seeded
  school and logged loudly (ADR-001 D-6).
- **RFC 7807 ProblemDetails** on every unhandled path, with a `traceId` to correlate against logs.
- **Swagger UI at the root — in Development only.**
- **An integration suite against real SQL Server** (Testcontainers), not an in-memory provider.

> A deliberately scoped first pass. RBAC, SIS import, recurring schedules, device registration,
> SignalR live dashboard, and PDF/CSV exports are **not** built yet (see plan §6, §15).

## Run

```bash
cd backend/EAMS.Api
dotnet run --urls "http://localhost:5080"
```

Then open **http://localhost:5080** for Swagger UI.

The connection string comes from `ConnectionStrings:EamsDb`. `appsettings.Development.json` points at
a local `.\SQLEXPRESS`; **the base `appsettings.json` deliberately carries no connection string**, so
any non-development host must supply `ConnectionStrings__EamsDb` through the environment or refuse to
start. That refusal is the point — a base-layer default would silently become the production default.

## Checks

The four CI runs, from the repo root:

```bash
dotnet build EAMS.sln -c Release
dotnet test  EAMS.sln -c Release     # integration half needs Docker running
cd web-admin && npm run lint && npm run build
```

## Mock data (seeded outside Production)

- 1 school: *University of San Agustin*
- 8 students, each with one RFID card (UIDs like `04A1B2C3`, `04A7B8C9`, …)
- 2 events: *University Convocation 2026* (**Open**) and *IT Week Seminar* (**Closed**)
- 2 attendance records already on the open event

## Key endpoints

| Method | Path | Notes |
|---|---|---|
| GET  | `/api/v1/students` | filters: `search`, `course`, `status` |
| GET  | `/api/v1/students/{id}` | detail + cards |
| GET  | `/api/v1/students/by-card/{cardUid}` | UID→student (UID is normalized: uppercase, separators stripped) |
| GET  | `/api/v1/events` | filter: `status` |
| GET  | `/api/v1/events/{id}/summary` | Present/Late/Absent/Excused + rate |
| GET  | `/api/v1/attendance` | filters: `eventId`, `studentId`, `status` |
| POST | `/api/v1/attendance/tap` | **core capture** — see below |
| POST | `/api/v1/attendance/manual` | organizer override (query params) |

### The tap flow (`POST /api/v1/attendance/tap`)

```json
{ "eventId": "<guid>", "cardUid": "04A7B8C9", "deviceId": null, "deviceTapId": "tap-abc-1", "tappedAt": null }
```

- Resolves the card UID → student; rejects unknown, inactive, and **soft-deleted-student** cards.
- Rejects taps on events that aren't `Open`, and `deviceId`s that aren't registered.
- Computes status: `Present` if within `StartAt + GraceMinutes`, else `Late`.
- **Idempotent** on the pair `(deviceId, deviceTapId)` — matching `UX_Attendance_Device_DeviceTapId`,
  not on `deviceTapId` alone (the keystone of offline sync, plan §8.2).
- `TimeInOut` events record a second tap as check-out.
- Both writers — tap and manual override — share one guarded insert, so a lost race returns the
  winning row rather than a 500.

Outcomes map to status codes exhaustively: `200` recorded/duplicate/checked-out/already-recorded,
`404` event/card/device not found, `400` event not open. There is no fall-through arm.

### Value sets

`Status`, `CaptureMethod`, `AttendanceMode` and `EventStatus` are `nvarchar` columns carrying closed
sets from plan §4. The sets are named in `EAMS.Domain/DomainValues.cs` — validate against those,
never against a literal. They are `const string` rather than enums on purpose: converting the columns
would be a data migration under the global no-DROP rule.

## Project layout

Layered per Technical Plan §3 — `Api → Application → Domain`, with `Infrastructure` implementing the
persistence side. No EF types leak into the API surface; controllers speak DTOs only, and every type
in `EAMS.Infrastructure` is `internal` except `AddEamsInfrastructure`, so a controller that reaches
for `EamsDbContext` is a **compile error (CS0122)**, not a review finding.

```
EAMS.Domain/
├── Entities.cs               # School, Student, RfidCard, Event, AttendanceRecord
├── DeviceEntities.cs         # Device (§4.10)
├── GroupingEntities.cs       # EventSchedules, StudentGroups, EventGroups (§4.6–§4.8)
├── RbacEntities.cs           # Users, Roles, Permissions (§4.11)
├── SisImportEntities.cs      # Import batches and rows (§4.12)
├── SystemEntities.cs         # AuditLogs, SystemSettings (§4.13)
├── DomainValues.cs           # the closed §4 value sets
├── CardUid.cs                # UID normalization
└── UtcTime.cs                # boundary UTC normalization
EAMS.Application/
├── Abstractions/             # IStudentService, IEventService, IAttendanceService, ISchoolContext
└── Dtos/Dtos.cs              # API request/response shapes
EAMS.Infrastructure/
├── Data/EamsDbContext.cs     # §4 schema: indexes, relationships, SchoolId query filters
├── Data/SeedData.cs          # dev convenience data
├── Migrations/               # Section4Baseline — the shipped schema
├── MultiTenancy/             # DevelopmentSchoolContext (the pre-auth tenant pin)
├── Services/                 # the service implementations
└── DependencyInjection.cs    # the single composition seam
EAMS.Api/
├── Controllers/              # Students, Events, Attendance
├── Authorization/            # HasPermissionNotEnforced + AuthorizationStatus (inert, by design)
└── Program.cs                # composition root: ProblemDetails, migrate/seed, gated Swagger
EAMS.Tests/
├── Unit/                     # pure logic, no database
└── Integration/              # real SQL Server via Testcontainers
```

## Next steps (when you want them)

1. JWT auth + permission-based RBAC (plan §11), which also replaces the pinned dev tenant.
2. Denormalize `SchoolId` onto `AttendanceRecords` so `UX_Attendance_Device_DeviceTapId` can be
   tenant-scoped — today the index is global while the reads around it are filtered. Harmless while
   one school is visible; see the note on `FindByDeviceTapAsync`.
3. Remaining modules: Groups, Schedules, Device registration, SIS Import, Reports, SignalR.
