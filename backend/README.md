# EAMS Backend — Mock Build (Core Slice)

A runnable .NET Web API implementing the **core slice** of the
[Events Attendance Monitoring System Technical Plan](../Events-Attendance-Monitoring-System-Technical-Plan.md):
Schools, Students, RFID Cards, Events, and the attendance **tap** capture flow.

## What's included

- **EF Core InMemory** database — no DB install needed; **seeded with mock data** on every startup.
- **Open/stubbed auth** — all endpoints are reachable without logging in (JWT/RBAC deferred).
- **Swagger UI** at the root URL.

> This is a deliberately scoped first pass. RBAC, SIS import, recurring schedules,
> devices, SignalR live dashboard, and PDF/CSV exports are **not** built yet (see plan §6).

## Run

```bash
cd backend/EAMS.Api
dotnet run --urls "http://localhost:5080"
```

Then open **http://localhost:5080** for Swagger UI.

## Mock data (seeded)

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
{ "eventId": "<guid>", "cardUid": "04A7B8C9", "deviceTapId": "tap-abc-1", "tappedAt": null }
```

- Resolves the card UID → student; rejects unknown/inactive cards.
- Rejects taps on events that aren't `Open`.
- Computes status: `Present` if within `StartAt + GraceMinutes`, else `Late`.
- **Idempotent** by `deviceTapId` — replaying the same tap returns the existing
  record instead of duplicating (the keystone of offline sync, plan §8.2).
- `TimeInOut` events record a second tap as check-out.

## Project layout

```
EAMS.Api/
├── Domain/Entities.cs        # School, Student, RfidCard, Event, AttendanceRecord
├── Data/EamsDbContext.cs     # DbContext + indexes/relationships
├── Data/SeedData.cs          # mock data
├── Dtos/Dtos.cs              # API request/response shapes
├── Controllers/              # Students, Events, Attendance
└── Program.cs                # InMemory DB + Swagger wiring
```

## Next steps (when you want them)

1. Real persistence (SQLite or SQL Server) + EF migrations.
2. JWT auth + permission-based RBAC (plan §11).
3. Remaining modules: Groups, Schedules, Devices, SIS Import, Reports, SignalR.
