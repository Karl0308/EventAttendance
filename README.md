# Events Attendance Monitoring System (EAMS)

Attendance monitoring for students with RFID integration — University of San Agustin / CICSS.
See the full [Technical Plan](Events-Attendance-Monitoring-System-Technical-Plan.md).

> **Status:** early mock build. Backend and frontend run independently on seeded mock data;
> they are not yet wired together.

## Repository layout

| Folder | What | Stack |
|---|---|---|
| [backend/](backend/) | REST API (core slice: students, events, RFID tap capture) | .NET 9 Web API, EF Core **InMemory** (seeded) |
| [web-admin/](web-admin/) | Admin SPA (dashboard, students, events, live attendance) | React 19 + TypeScript + Vite + MUI |

Each folder has its own README with run instructions.

## Quick start

**Backend** → http://localhost:5080 (Swagger at root)
```bash
cd backend/EAMS.Api
dotnet run --urls "http://localhost:5080"
```

**Web admin** → http://localhost:5173
```bash
cd web-admin
npm install
npm run dev
```

## Notes

- The backend uses an **in-memory** database seeded on startup — data resets each run.
- The web admin runs on its **own mock data** via `src/api.ts`; connecting it to the backend
  later means changing only that file.
