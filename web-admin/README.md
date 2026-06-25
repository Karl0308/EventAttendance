# EAMS Web Admin — Mock Build (Frontend Only)

Standalone React web admin for the
[Events Attendance Monitoring System](../Events-Attendance-Monitoring-System-Technical-Plan.md) (plan §7).
**Runs entirely on mock data** — no backend required. The backend in [../backend](../backend) is
separate; we'll wire them together later.

## Stack

- **React 19 + TypeScript + Vite**
- **MUI v6** (`Grid2`) + **MUI X DataGrid v7**
- **React Router v6**

## Run

```bash
cd web-admin
npm install      # first time only
npm run dev      # http://localhost:5173
```

## Pages

| Route | Page |
|---|---|
| `/` | **Dashboard** — KPIs (students, open events, checked-in) + event list |
| `/students` | **Students** — searchable DataGrid with RFID card column |
| `/events` | **Events** — event cards with status/mode chips |
| `/events/:id` | **Event detail** — live attendance board, stat cards, **Simulate RFID tap** |

### Simulate RFID tap

On an **Open** event, pick a card/student and hit **Tap**. The mock `api.tap()` resolves the
UID → student, applies the grace-period logic (`Present` vs `Late`), and the attendance board +
stat cards update live — the same behavior the real `/attendance/tap` endpoint provides.

## How the mock is wired (and how we connect later)

```
src/
├── types.ts          # API-shaped types (mirror backend DTOs)
├── mock/db.ts        # in-memory seed data (matches backend seed)
├── api.ts            # ← async facade. ALL data access goes through here.
├── theme.ts          # USA red/gold MUI theme
├── components/Layout.tsx
└── pages/            # Dashboard, Students, Events, EventDetail
```

Every component calls `api.*` (async). To connect to the real backend later, **only `src/api.ts`
changes** — replace each method body with `fetch("/api/v1/...")`. The types and components stay put.

> Mock data resets on page reload (it lives in memory).
