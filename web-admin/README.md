# EAMS Web Admin

React admin SPA for the
[Events Attendance Monitoring System](../Events-Attendance-Monitoring-System-Technical-Plan.md) (plan §7).
It talks to the real .NET API in [../backend](../backend) through one seam, `src/api.ts` — nothing in
a component reaches around it.

## Stack

- **React 19 + TypeScript + Vite**
- **MUI v6** + **MUI X DataGrid v7**
- **React Router v7**
- **Vitest** (`test/`), **oxlint**

## Run

```bash
cd web-admin
npm install                       # first time only
npm run dev                       # http://localhost:5173
```

The API has to be running too, on the port the dev proxy expects:

```bash
cd backend/EAMS.Api && dotnet run --urls "http://localhost:5080"
```

### The SPA and the API must be one origin — that is what the dev proxy is for

`npm run dev` serves `/api/*` from the API host (`vite.config.ts` → `server.proxy`), so the browser
sees one origin on `:5173`. That is not a convenience: a signed-in session's refresh token is an
`httpOnly`, `Secure`, `SameSite=Strict` cookie scoped to the API's `/api/v1/auth` path
([`AuthCookies.cs`](../backend/EAMS.Api/Authentication/AuthCookies.cs)), and a cross-origin SPA cannot
use one without the API opting into credentialed CORS — a wider hole than it closes.

So `src/api.ts` defaults to the **root-relative** `/api/v1`. Point the proxy at a different API with
`VITE_DEV_API_TARGET`; point a *built* bundle at a different path with `VITE_API_BASE_URL`. See
[`.env.example`](./.env.example) — every variable is documented there.

## Checks

The same four CI runs. Run all of them before calling a change done:

```bash
npm run lint
npm run test
npm run build
node ../scripts/generate-endpoint-index.mjs   # docs/api/endpoints.md must already be current
```

## Signing in

`/login` is the only anonymous route; everything else redirects there. How it works, and the two
things about it that are not the usual pattern:

| Credential | Where it lives | Who can read it |
|---|---|---|
| Refresh token (`eams_rt`) | `httpOnly` cookie, `Path=/api/v1/auth` | The browser only. **No script, ever.** |
| Access token | A JavaScript variable in `src/authSession.ts` | `src/api.ts`, to compose one header |
| CSRF token (`eams_csrf`) | Readable cookie, `Path=/` | Echoed in `X-CSRF-Token` on refresh/logout |

1. **A reload always loses the access token, and that is by design.** It is in memory and in nothing
   else — not `localStorage`, not `sessionStorage`, both of which any injected script can read. Every
   page load therefore starts by calling `POST /auth/refresh` to recover the session; `RequireAuth`
   shows a loading state while that runs rather than redirecting, so F5 does not cost you your route.
2. **A 401 renews the session once and replays the request once.** Concurrent 401s share a single
   in-flight refresh (`renewSession` in `src/api.ts`). This is not an optimisation: rotation is
   single-use and a second presentation of an already-rotated token is read as a **replay**, which
   revokes the whole token family. A stampede of parallel refreshes signs the user out of every tab.
   `test/apiSession.test.ts` pins it.
3. A refresh the server *refuses* clears the session and the router lands on `/login`. A refresh that
   could not *reach* the API deliberately does not — a dropped connection is not evidence that a
   session ended.

`<PermissionGuard permission={PERMISSIONS.devicesRead}>` hides routes and nav entries the token
cannot use. It is UI convenience only; the server checks the same code again and is authoritative.

### Simulate RFID tap (development builds only)

On an **Open** event, paste a device key on the page and press **Tap**. That path is deliberately
separate from the session above: a kiosk sends `Authorization: DeviceKey …` and is scoped to
`attendance.capture`; a person sends `Bearer`. A 401 there is a *device key* and renews nothing.

## Layout

```
src/
├── types.ts          # API-shaped types (mirror backend DTOs)
├── api.ts            # ← the seam. ALL data access goes through here, and the only place a
│                     #   credential is composed into a header.
├── authSession.ts    # the access token + who it speaks for. No fetch, no React.
├── authContext.ts    # useAuth / useSignedInUser
├── apiGuidance.ts    # what a failure means and whether Retry is worth offering
├── permissions.ts    # the permission codes, named once
├── theme.ts          # USA red/gold MUI theme
├── components/       # Layout, AuthProvider, RequireAuth, PermissionGuard, dialogs
└── pages/            # Login, Dashboard, Students, Events, EventDetail, Devices, Terms
```

## Deploying

IIS, same site as the API. [`public/web.config`](./public/web.config) ships in `dist/` and carries the
deep-link fallback (without it, refreshing `/students/import` returns an IIS 404) and the
Content-Security-Policy. Set `VITE_BASE_URL` to the application's virtual path before building.

> The GitHub Pages deployment this project used to run was removed along with the login screen: the
> app's home is IIS, same-origin with the API, and a public URL serving an authenticated login form
> pointed at a `localhost` API was not wanted. `web.config` is now the only deep-link fallback.
