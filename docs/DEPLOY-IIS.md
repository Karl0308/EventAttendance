# Deploying EAMS to IIS — dev.iloilosupermart.com

Two IIS applications under **Default Web Site**:

| App | Virtual path | Physical path | Serves |
|---|---|---|---|
| API | `/eamsapi` | `C:\inetpub\eams\api` | .NET 9 Web API |
| Admin SPA | `/eams` | `C:\inetpub\eams\web` | Static React bundle |

Resulting URLs: SPA at `https://dev.iloilosupermart.com/eams/`, API at
`https://dev.iloilosupermart.com/eamsapi/api/v1/...`.

> **These paths are baked into the SPA bundle at build time.** `web-admin/.env.production` holds
> them. Changing either path means editing that file and running `npm run build` again — nothing on
> the server can repoint an existing bundle.

---

## 0. Access — this build has no authentication

Every endpoint is open except the five device-key capture routes: anyone who reaches `/eamsapi` can
read every student record, create and delete events, and regenerate device keys. The API logs this on
every startup, and ADR-001 D-6/D-28 hold it to development use until Technical Plan §11 lands.

This host is dev-testing only, which is the environment that constraint assumes. The one thing worth
checking is whether `dev.iloilosupermart.com` resolves from outside your network — if it does, add an
IP allow-list (IIS Manager → *IP Address and Domain Restrictions* → deny unlisted, allow your
office/VPN range), because any real student data loaded for testing is readable by anyone who finds
the URL. If the name is internal-only, you are already behind that boundary.

Revisit this before the system is used for anything but testing.

---

## 1. Server prerequisites

- **.NET 9 Hosting Bundle** — <https://dotnet.microsoft.com/download/dotnet/9.0> → "Hosting Bundle"
  (not the SDK, not the runtime alone). It installs the ASP.NET Core Module IIS needs.
  **Run `iisreset` after installing it**, or IIS will 500.19 with `ANCM` errors.
- IIS with *Static Content* enabled (for the SPA).
- SQL Server reachable from the VM, with the `EAMS` database creatable by the login you use.

Verify the module registered:

```powershell
Import-Module WebAdministration
Get-WebGlobalModule | Where-Object Name -like "*AspNetCore*"
```

---

## 2. Deploy the API

Copy `publish\eamsapi\*` to `C:\inetpub\eams\api`.

Create the application:

```powershell
Import-Module WebAdministration

New-WebAppPool -Name "EamsApi"
# .NET 9 runs out-of-process; the pool hosts no managed code itself.
Set-ItemProperty IIS:\AppPools\EamsApi -Name managedRuntimeVersion -Value ""

New-WebApplication -Site "Default Web Site" -Name "eamsapi" `
  -PhysicalPath "C:\inetpub\eams\api" -ApplicationPool "EamsApi"
```

### Configuration — connection string and CORS

**Do not put the SQL password in `appsettings.json`.** `appsettings.json` deliberately ships with no
connection string so a missing one fails loudly instead of silently connecting to the wrong database.
Set it as an app-pool environment variable instead — it stays out of the repo and out of any file a
directory-listing bug could serve:

```powershell
$pool = "IIS:\AppPools\EamsApi"

# Connection string. Replace <PASSWORD> — do not paste a real password into a script you keep.
Set-WebConfigurationProperty -PSPath $pool -Filter "environmentVariables" `
  -Name "." -Value @{name="ConnectionStrings__EamsDb"; value="Server=.;Database=EAMS;User Id=sa;Password=<PASSWORD>;TrustServerCertificate=True"}

# The SPA's origin. Without this the API admits NO origin outside Development and every
# browser request fails CORS while the server logs nothing.
Set-WebConfigurationProperty -PSPath $pool -Filter "environmentVariables" `
  -Name "." -Value @{name="Cors__AllowedOrigins__0"; value="https://dev.iloilosupermart.com"}

# Environment. See the warning below before choosing.
Set-WebConfigurationProperty -PSPath $pool -Filter "environmentVariables" `
  -Name "." -Value @{name="ASPNETCORE_ENVIRONMENT"; value="Development"}

Restart-WebAppPool -Name "EamsApi"
```

`Cors__AllowedOrigins__0` is the **scheme + host only** — no path, no trailing slash. A browser origin
never includes a path, and `https://dev.iloilosupermart.com/eams` will silently match nothing.

### ⚠️ Choosing ASPNETCORE_ENVIRONMENT

Three behaviours are gated on `IsDevelopment()`, and this decides all three at once:

| | `Development` | `Production` |
|---|---|---|
| Swagger UI at `/eamsapi/` | yes | **no** |
| Database seeding (school, **term**, students, kiosk device) | yes | **no** |
| Well-known development kiosk key | seeded | **not seeded** |

**The term still matters, but it is no longer a dead end.** The roster importer takes a `TermId` as
input, so with no term the import page's term picker is empty and **roster import cannot be started**.
On `Production` you get a migrated but empty database and that is exactly the state you are in.

The difference from earlier builds is that there is now a screen for it: the admin SPA's **Terms** page
(`/terms`, over `POST /api/v1/academic/terms`, `PUT /api/v1/academic/terms/{id}` and
`PATCH /api/v1/academic/terms/{id}/current`) creates a term, edits it, and moves the current-term flag.
So the first-run order on a `Production` install is: open Terms, create the term, make it current, then
import the roster. **Do not insert a `Terms` row by hand** — the page enforces the code and date rules
the API checks and puts the current-term flag where exactly one term can hold it.

So: use **`Development`** if you want a working demo with seed data and Swagger — accepting that it
also seeds a publicly-known device key, which is only acceptable behind the gate from §0. Use
`Production` for a clean database, and create the term through the Terms page on first run.

(`Production` also needs a `Schools` row first — the tenancy filter hides everything without one, the
Terms page included.)

### Database

The app runs EF migrations on startup, so the schema is created automatically on first request. The
login in the connection string needs rights to create the database, or you create `EAMS` empty first
and grant `db_owner`.

**Rotate the `sa` password.** It was shared in plain text over chat. Better still, create a dedicated
login scoped to `EAMS` rather than deploying with `sa` — a SQL injection or config leak in a build
with no authentication currently reaches the whole instance.

---

## 3. Deploy the SPA

Copy `web-admin\dist\*` to `C:\inetpub\eams\web`. It already contains `web.config` — that file is
what makes deep links work, so confirm it copied.

```powershell
New-WebAppPool -Name "EamsWeb"
Set-ItemProperty IIS:\AppPools\EamsWeb -Name managedRuntimeVersion -Value ""

New-WebApplication -Site "Default Web Site" -Name "eams" `
  -PhysicalPath "C:\inetpub\eams\web" -ApplicationPool "EamsWeb"
```

### How the routing works

Three prefixes must agree, and a mismatch in any one produces a blank page rather than an error:

1. **`VITE_BASE_URL=/eams/`** in `.env.production` → Vite's `base` → asset URLs in `index.html`
   (`/eams/assets/index-*.js`) and `import.meta.env.BASE_URL`.
2. **`<BrowserRouter basename={import.meta.env.BASE_URL}>`** in `src/main.tsx` — so react-router
   strips `/eams` before matching. Already wired; it reads the same value as the assets, so the two
   cannot drift.
3. **The IIS virtual path** `/eams`.

The `web.config` handles the fourth piece — the server. IIS resolves `/eams/students/import` against
the filesystem, finds nothing, and 404s; the `httpErrors` rule returns `index.html` instead so the
SPA boots and react-router reads the path. Without it the app works when clicked through and breaks
on refresh or a pasted link.

This mirrors `C:\EveryoneWorkspace\CSSINew\web` — same `VITE_BASE_URL` → `base` → `basename` chain,
same `public/web.config` copied into `dist/`.

---

## 4. Verify

```powershell
# API — 200 and a JSON array
Invoke-WebRequest "https://dev.iloilosupermart.com/eamsapi/api/v1/students" -UseBasicParsing

# SPA root — 200
Invoke-WebRequest "https://dev.iloilosupermart.com/eams/" -UseBasicParsing

# Deep link — 200 with index.html. This is the web.config test; a 404 here means it did not copy.
Invoke-WebRequest "https://dev.iloilosupermart.com/eams/students/import" -UseBasicParsing
```

Then in a browser at `/eams/`: the student list loads (API + CORS both work), and the Students page's
**Import roster** button opens a page whose term dropdown is populated (seeding worked).

### If it breaks

| Symptom | Cause |
|---|---|
| Blank page, 404s on `/eams/assets/*.js` in the console | `VITE_BASE_URL` ≠ IIS virtual path. Rebuild. |
| Works clicking through, 404 on refresh | `web.config` missing from `C:\inetpub\eams\web`. |
| List empty, console shows CORS error | `Cors__AllowedOrigins__0` unset or has a path/trailing slash. |
| HTTP 500.19 | Hosting Bundle not installed, or `iisreset` not run after it. |
| HTTP 500.30 | App failed to start — almost always the connection string. Check Event Viewer → Windows Logs → Application. |
| Term dropdown empty on import | `ASPNETCORE_ENVIRONMENT=Production`, so seeding was skipped. See §2. |

---

## 5. Rebuilding

```powershell
# API
dotnet publish backend\EAMS.Api\EAMS.Api.csproj -c Release -o publish\eamsapi

# SPA — picks up .env.production automatically
cd web-admin; npm ci; npm run build
```

Stop the app pool before copying over `C:\inetpub\eams\api` — IIS locks the DLLs while running.
