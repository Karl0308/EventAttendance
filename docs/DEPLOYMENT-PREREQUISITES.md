# Deployment prerequisites

**Read this before deploying EAMS to any server.** It is the checklist of things that must be true
*before* the first `dotnet publish` — the settings whose absence stops the host from starting, the
server version that decides whether half the application works, and the account you will sign in
with once it does.

Step-by-step IIS instructions live in [`DEPLOY-IIS.md`](DEPLOY-IIS.md). This document is what you
gather first; that one is what you do.

---

## 1. Checklist

Nothing here is optional, and the first four produce a dead site rather than a degraded one.

| # | Prerequisite | Failure if missing |
|---|---|---|
| 1 | `Jwt__SigningKey` on the app pool | **App will not start.** HTTP 500.30 |
| 2 | `ConnectionStrings__EamsDb` on the app pool | **App will not start** |
| 3 | `Cors__AllowedOrigins__0` on the app pool | Every browser request fails CORS; server logs nothing |
| 4 | ASP.NET Core 9 Hosting Bundle installed | HTTP 500.19 / 502.5 |
| 5 | SQL Server **2016 or newer** (see §3) | Works on 2012 only because of a compatibility pin |
| 6 | The SPA deployed in the same window as the API | Login breaks in ways neither side explains |
| 7 | A database the app login can create tables in | Migrations fail on startup |

---

## 2. The four app-pool environment variables

.NET maps `__` (double underscore) to the `:` in a configuration key, so `Jwt__SigningKey` is
`Jwt:SigningKey`. These are set on the **application pool**, not in `appsettings.json` — that file is
the base layer for *production* defaults, and a secret placed there is a secret in the repository.

Run as administrator on the server:

```powershell
$pool = "IIS:\AppPools\EamsApi"

# 1. Connection string.
Set-WebConfigurationProperty -PSPath $pool -Filter "environmentVariables" `
  -Name "." -Value @{name="ConnectionStrings__EamsDb"; value="Server=.;Database=EAMS;User Id=<LOGIN>;Password=<PASSWORD>;TrustServerCertificate=True"}

# 2. JWT signing key — see section 4. THE APP POOL WILL NOT START WITHOUT THIS.
Set-WebConfigurationProperty -PSPath $pool -Filter "environmentVariables" `
  -Name "." -Value @{name="Jwt__SigningKey"; value="<paste the generated key>"}

# 3. The SPA origin — scheme + host only. No path, no trailing slash.
Set-WebConfigurationProperty -PSPath $pool -Filter "environmentVariables" `
  -Name "." -Value @{name="Cors__AllowedOrigins__0"; value="https://dev.iloilosupermart.com"}

# 4. Environment. See DEPLOY-IIS.md before choosing — this one has consequences.
Set-WebConfigurationProperty -PSPath $pool -Filter "environmentVariables" `
  -Name "." -Value @{name="ASPNETCORE_ENVIRONMENT"; value="Production"}

Restart-WebAppPool -Name "EamsApi"
```

Verify they took:

```powershell
Get-WebConfiguration -PSPath "IIS:\AppPools\EamsApi" -Filter "environmentVariables" |
  Select-Object -ExpandProperty Collection | Select-Object name, value
```

> **App-pool `environmentVariables` needs IIS 10 / Windows Server 2016 or newer.** If the command
> above returns nothing on an older server, the fallback is an `<environmentVariables>` block inside
> the published `web.config` — which puts the signing key on disk in the application directory. Treat
> that file as a secret if you go that route.

---

## 3. SQL Server version — read this one

**The application is pinned to SQL Server 2012 semantics, and that pin is load-bearing.**

`DependencyInjection.SqlServerCompatibilityLevel` is `110`. EF Core 9 normally translates a
parameterised `.Contains(collection)` into `OPENJSON`, which requires SQL Server 2016 *and* a
database at compatibility level 130 or higher. The university server is 2012, whose ceiling is 110,
so `OPENJSON` is unavailable there.

EF never asks the server what version it is — it assumes a recent one. So the mismatch **cannot fail
at startup**. It fails on the first query that filters by a list, as a bare HTTP 500 with no detail
in the response body. It was found as a roster import that died in 11 milliseconds having written
nothing, and the same translation sits under 18 query sites across event audience resolution, the
derived student groups, and the importer.

**What this means for you:**

- On SQL Server 2012 everything works because of the pin. Do not remove it.
- On SQL Server 2016+ everything still works — the pin only costs query-plan efficiency, because
  collections are inlined as literals instead of passed as one parameter.
- **When the server is upgraded**, raise or delete the constant. It is the only thing holding this
  codebase to a 2012 dialect, and it is deliberately not conditional on environment or configuration
  so that the test suite generates the same SQL the server will run.

> SQL Server 2012 left extended support in **July 2022**. It is unpatched and holds student names,
> e-mail addresses, and RFID serials. Upgrading is a security matter, not a performance one.

Check what you are on:

```sql
SELECT @@VERSION;
SELECT name, compatibility_level FROM sys.databases WHERE name = DB_NAME();
```

---

## 4. The JWT signing key

`Jwt__SigningKey` is a **hard startup requirement**. The host throws rather than starting on a key
that is missing, shorter than 32 bytes (HMAC-SHA256 floor, RFC 7518 §3.2), or that pattern-matches a
known placeholder such as `changeme` or `your-256-bit-secret`.

There is deliberately **no generated fallback**. A key minted at startup would rotate on every
app-pool recycle and silently invalidate every session that was live at the time — a failure that
looks like a bug in login rather than a missing setting.

### Generate one

```powershell
$bytes = New-Object byte[] 64
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
[Convert]::ToBase64String($bytes)
```

Or with OpenSSL: `openssl rand -base64 64`

### Rules

- **A fresh key per host.** Never reuse one between development, staging, and production.
- **Never commit it.** Not to `appsettings.json`, not to a script kept in the repository.
- **Rotating it signs everyone out.** Every access and refresh token becomes invalid immediately.
  That is the correct emergency response to a suspected leak, and it is disruptive by design.

### Local development

Not an environment variable — user secrets, so it stays off disk inside the repository:

```bash
cd backend/EAMS.Api
dotnet user-secrets set "Jwt:SigningKey" "<64+ random characters>"
```

---

## 5. The administrator account

### Username

```
dev-admin@usa.edu.ph
```

This address is the constant `SeedData.DevelopmentSuperAdminEmail` and is the same everywhere.

### Password — configuration, never a value in this repository

**There is no default password, no generated one, and no password written in this file.** That is
deliberate, and a test enforces it. A SuperAdmin credential is not scoped to anything: it mints
device keys and decides which semester the institution is in. This repository is public, so a working
administrator password committed here would be a working administrator password on the internet.

**Ask whoever set up the environment for the password. It is not recoverable from source.**

#### Development — seeded from configuration

```bash
cd backend/EAMS.Api
dotnet user-secrets set "Seed:DevelopmentSuperAdminPassword" "<12+ characters>"
```

On the next start a Development host creates `dev-admin@usa.edu.ph` with that password. Omit the
setting and the seed is skipped, loudly, in the log. Minimum length is 12, and there are no
composition rules — length beats punctuation.

Three things that surprise people:

1. **It is one-shot per address.** The seed checks whether the user exists and returns if it does.
   Changing the secret afterwards does **not** change the password — it silently does nothing. To
   change it, drop the database and let it re-seed, or use `create-admin` with a different address.
2. **A failed seed does not stop the host.** A password under 12 characters produces a `LogWarning`
   and the app starts anyway. Watch the log for `The Development SuperAdmin was not seeded` rather
   than assuming a clean boot means an account exists.
3. **It never runs in Production.** The seed refuses on a Production host, independently of its
   caller.

> **Setting this secret turns one test red.** `RbacSeedTests.A_development_host_seeds_no_super_admin_when_no_password_is_configured`
> clears the *environment variable* but cannot clear the *user-secrets* entry, so a developer who
> follows the setup above sees a local failure that CI never reproduces. Remove the secret before a
> full `dotnet test` run; the already-seeded account keeps working, because the password is stored
> hashed in the database and the secret is only read at creation.

#### Production — the CLI, not a seed

```bash
dotnet EAMS.Api.dll create-admin --email admin@usa.edu.ph --name "Full Name" --role SuperAdmin
```

`--email` and `--name` are required. `--role` is optional and defaults to `SchoolAdmin` — pass
`SuperAdmin` explicitly for the first account. `--school <code>` is needed only when the database
holds more than one school.

It prompts for the password twice, hidden. There is **no `--password` flag** and there will not be
one: a password given as an argument lands in shell history, the process table, and CI logs. For an
unattended run, pipe it on standard input or set `EAMS_BOOTSTRAP_PASSWORD`.

An existing e-mail address is refused, never reset — so this is safe to re-run.

**This is the recommended way to create an administrator on any server**, in any environment. It
needs no `Seed__` variable, is not subject to the one-shot rule above, and does not depend on the
host running in Development.

##### It needs the environment variables in *your shell*, not just on the app pool

This is the step that catches people. App-pool environment variables belong to the IIS worker
process; a console session does not see them. And `create-admin` runs on the fully built host — the
JWT settings are resolved at startup, before the command branch is reached — so it refuses to start
without a signing key even though it never mints a token.

Set both in the shell before running it, from the published application directory:

```powershell
cd C:\inetpub\wwwroot\eamsapi   # wherever the API was published

$env:ConnectionStrings__EamsDb = "Server=.;Database=EAMS;User Id=<LOGIN>;Password=<PASSWORD>;TrustServerCertificate=True"
$env:Jwt__SigningKey            = "<the same key that is on the app pool>"

dotnet EAMS.Api.dll create-admin --email admin@usa.edu.ph --name "Full Name" --role SuperAdmin
```

Without the connection string it cannot reach the database; without the signing key the host throws
before the command runs. Both failures name the missing setting, but neither obviously points at
"you are running this outside the app pool".

---

## 6. Deploy order

**The API and the SPA must ship in the same window.** The admin app now sends its API requests to a
root-relative URL so session cookies are same-origin, and it has a login screen that expects `/auth`
to exist. An API with authentication paired with an older SPA — or the reverse — breaks in ways that
neither side's error messages explain.

1. Set the environment variables (§2) **first**. The app pool will not start otherwise, and a failed
   start before the settings exist wastes a diagnostic cycle.
2. Publish and deploy the API.
3. Publish and deploy the SPA.
4. Verify (§7).

### Migrations run on startup

The application applies pending migrations itself when it starts. **A deployment therefore changes
the database schema whether or not you thought about it.** Migrations here are additive and never
destructive, but take a backup before deploying a build that is more than a few commits ahead of
what is running.

---

## 7. Verify

```bash
# API answers
curl -i https://dev.iloilosupermart.com/eamsapi/api/v1/events

# Auth routes exist — a 404 here means an older build is deployed
curl -i -X POST https://dev.iloilosupermart.com/eamsapi/api/v1/auth/login \
  -H "Content-Type: application/json" -d '{"email":"x@y.z","password":"wrong"}'

# SPA root, then a deep link — the deep link is the web.config rewrite test
curl -i https://dev.iloilosupermart.com/eams/
curl -i https://dev.iloilosupermart.com/eams/students
```

Then sign in through the browser and confirm the session survives a **full browser restart**. That
path exercises the refresh cookie and its CSRF pair together, and it is the one that historically
broke.

---

## 8. When it breaks

| Symptom | Cause |
|---|---|
| HTTP 500.30 on startup | Missing or short `Jwt__SigningKey`, or the connection string. Both name the setting in the message — Event Viewer → Windows Logs → Application |
| HTTP 500.19 / 502.5 | ASP.NET Core Hosting Bundle missing, or installed before IIS |
| Browser requests all fail, server logs nothing | `Cors__AllowedOrigins__0` missing, or carries a path |
| A 500 on import, event audience, or student groups, with no detail | The SQL Server compatibility pin (§3) — check the server version first |
| Signed out on every browser restart | The refresh cookie `Path` does not match what the browser sees. The API logs the resolved path once, the first time a cookie is issued |
| Login works, everything else 401s | The SPA and API are from different builds — see §6 |

---

## Related

- [`DEPLOY-IIS.md`](DEPLOY-IIS.md) — step-by-step IIS deployment
- [`TESTING_GUIDE.md`](TESTING_GUIDE.md) — running the system locally (developers)
- [`QA-OVERVIEW.md`](QA-OVERVIEW.md) — testing a deployed instance (no toolchain required)
