# Porting EAMS to PostgreSQL + Railway

**Read this first. It is written for a fresh session in the *forked* repo, with no memory of the
conversation that produced it.**

## What this is

EAMS currently runs on **SQL Server + IIS** (`dev.iloilosupermart.com`). That deployment is staying
exactly as it is. This document covers a **separate fork** that gets converted to **PostgreSQL** and
deployed to **Railway**.

Two repos, one database provider each. There is deliberately **no** provider-conditional code, no
parallel migration sets, and no `if (isPostgres)` anywhere. If you find yourself writing that, stop —
you are in the wrong repo or solving the wrong problem.

| | Original repo | This repo (the fork) |
|---|---|---|
| Database | SQL Server | PostgreSQL |
| Host | IIS on Windows | Railway (Linux containers) |
| Status | Frozen / wind-down | Active |

### Ground rules that do not change

- The **Technical Plan is still the source of truth** for schema and API shape. The provider changes;
  entity names, columns and endpoints do not.
- **Never write data-loss SQL.**
- The **tap flow stays idempotent by `deviceTapId`**. It is the foundation of planned offline mobile
  sync (plan §8.2). Do not weaken it during the port.
- **Card UIDs stay normalized** (uppercase, separators stripped) on both sides.

---

## Phase 0 — Repo setup

- [ ] Fork or `git clone` the original — **do not** copy files. History matters, and so does the
      ability to pull fixes across later.
- [ ] Add the upstream remote:
      `git remote add upstream https://github.com/Karl0308/EventAttendance`
- [ ] Rename the repo to something unmistakable (e.g. `EventAttendance-Railway`). Two repos with
      identical file trees is how commits land in the wrong place.
- [ ] Work on a branch, not `main`.

## Phase 1 — Delete what is now false

Stale instructions are worse than none — they mislead humans and agents alike. Do this **before**
writing code, so nothing you read later is lying to you.

- [ ] `docs/DEPLOY-IIS.md` — delete. App pools, `web.config` environment variables, `publish\eamsapi`.
      None of it applies.
- [ ] `backend/EAMS.Api/web.config` — delete. IIS-only. (It also carries WebDAV `remove` lines fixing
      a Windows-only 405 on PUT/DELETE; that problem does not exist on Linux.)
- [ ] `web-admin/public/web.config` — delete. The SPA deep-link fallback is replaced by Caddy in
      Phase 5.
- [ ] `web-admin/.env.production` — rewrite. It hardcodes `dev.iloilosupermart.com`. **Note it is
      git-ignored** (`.gitignore` line 34, `.env.*`), so it will not arrive with the fork — you must
      create it by hand.
- [ ] `CLAUDE.md` — rewrite the persistence, run and deployment sections. It currently states SQL
      Server, migrations-first against `Migrations/Section4Baseline`, Testcontainers SQL Server, and
      `scripts/dev-setup.ps1`. All of that changes.
- [ ] `scripts/dev-setup.ps1` — PowerShell-only, sets user-secrets. Decide whether to keep it or
      replace it with something cross-platform; Railway builds run on Linux.

## Phase 2 — Spike before committing (half a day)

**Do not skip this.** It sizes the real work far better than any estimate.

- [ ] Swap `Microsoft.EntityFrameworkCore.SqlServer` for `Npgsql.EntityFrameworkCore.PostgreSQL` in
      `backend/EAMS.Infrastructure`.
- [ ] Point `AddEamsInfrastructure` at `UseNpgsql`.
- [ ] Run `dotnet ef migrations add Baseline --project backend/EAMS.Infrastructure`.
- [ ] Write down every error. That list is the actual backlog.

Expect `HasColumnType("rowversion")` and the bracket-quoted index filters to fail first.

## Phase 3 — The port

Delete the SQL Server migrations outright and generate **one fresh PostgreSQL baseline**. This is the
main payoff of the two-repo split: no parallel migration sets, one clean history.

- [ ] Delete `backend/EAMS.Infrastructure/Migrations/` entirely (9 migrations + snapshot, ~22,770
      lines). Preserve the *intent* of `Section4Baseline` — the schema should stay readable against
      Technical Plan §4.

Then, hardest first.

### 3.1 The attendance cursor — the hard one

`AttendanceRecord.RowVersion` is a SQL Server `rowversion`, and it is **load-bearing** for the live
dashboard (Phase 4d, D-30).

- [ ] **Read `backend/EAMS.Domain/AttendanceCursor.cs` in full before touching anything.** Its
      comments are the real spec: why `UpdatedAt` fails as a cursor (ties inside one tick, and clocks
      that move backwards), and why the wire format is base64**url** rather than standard base64.
- [ ] Replace with a `bigint` column backed by a dedicated sequence.
- [ ] Add a trigger that bumps it on **INSERT *and* UPDATE**. A plain `DEFAULT nextval(...)` only
      covers insert; `rowversion` advances on both, and the delta query depends on that.
- [ ] Keep the column 8 bytes. `bigint` is 8 bytes, so `AttendanceCursor`'s base64 encoding survives
      unchanged and **existing mobile clients keep working**. Do not change the wire format.
- [ ] Preserve `IX_Attendance_EventId_RowVersion` (`EamsDbContext.cs` ~line 812). The delta query in
      `EventService.cs` (~lines 737-774) is written to match that index.

Current definition, for reference — `EamsDbContext.cs` ~lines 800-804:

```csharp
e.Property(x => x.RowVersion)
    .HasColumnType("rowversion")
    .ValueGeneratedOnAddOrUpdate()
    .IsConcurrencyToken(false);   // deliberately NOT .IsRowVersion()
```

`IsConcurrencyToken(false)` is intentional — flipping it turns ordinary updates into
`DbUpdateConcurrencyException`. Keep that property.

> **Known hazard — verify during the port.** Neither SQL Server `rowversion` nor a PostgreSQL sequence
> is gap-free or commit-ordered: a row committed later can carry a *lower* value when two transactions
> interleave, and a `> lastSeen` cursor would skip it. `EventService.cs` already reads with a
> `ceiling` / `max` bound (~lines 278, 737). Confirm that bound still closes the gap under Postgres
> before calling this done.

### 3.2 Case sensitivity

SQL Server's default collation is case-**in**sensitive. PostgreSQL is case-**sensitive**. This breaks
silently — no error, just duplicate accounts and lookups that quietly miss.

- [ ] `backend/EAMS.Infrastructure/Identity/UserProvisioningService.cs:69` — `u.Email == email` with
      **no normalization**. On Postgres, `Admin@usa.edu.ph` and `admin@usa.edu.ph` become two
      different users. Fix this.
- [ ] `UserCredentialVerifier.cs:149` already lowercases before querying (`:62` uses the normalized
      value). Login is safe. Use it as the pattern.
- [ ] Audit **every** string equality that reaches the database for the same problem.
- [ ] Card UIDs are already normalized in code — verify, do not assume.

### 3.3 `DateTime` handling

- [ ] 26 `DateTime` properties in `EAMS.Domain` (11 non-nullable, 15 nullable). **Zero**
      `DateTimeOffset`.
- [ ] Npgsql maps `DateTime` to `timestamptz` only when `Kind == Utc`, and **throws** otherwise.
      Anything arriving as `Unspecified` is a runtime failure, not a compile error.
- [ ] Audit every write path before cutover.

### 3.4 Unique indexes and NULL semantics

- [ ] **18** unique indexes declare `HasFilter(null)`. SQL Server permits exactly **one** NULL in a
      unique index; PostgreSQL permits **unlimited**. Any of those 18 on a nullable column silently
      loses its constraint.
- [ ] Where the constraint must hold, use Postgres 15+ `NULLS NOT DISTINCT`, or add a partial index.

### 3.5 Filtered indexes (mechanical)

11 filtered indexes in `EamsDbContext.cs`. Postgres supports partial indexes, so all of them
translate — only the syntax changes: `[Brackets]` become `"double quotes"`, and `bit` comparisons
become boolean.

```
HasFilter("[ApiKey] IS NOT NULL")               HasFilter("[ExternalId] IS NOT NULL")
HasFilter("[ApiKeyId] IS NOT NULL")             HasFilter("[StudentGroupId] IS NOT NULL")
HasFilter("[CheckOutDeviceTapId] IS NOT NULL")  HasFilter("[StudentId] IS NOT NULL")
HasFilter("[DeviceTapId] IS NOT NULL")          HasFilter("[SourceType] = 'Derived'")
HasFilter("[IsActive] = 1")  x2                 HasFilter("[IsCurrent] = 1")
```

The `IS NOT NULL` ones are the SQL Server idiom for "allow many NULLs". Postgres does that natively,
so the filter is redundant there — but keep it: it still expresses intent and keeps the index small.

### 3.6 What you do NOT have to port

**There is no raw SQL anywhere outside migrations.** No `FromSqlRaw`, `ExecuteSqlRaw`,
`FromSqlInterpolated` or `ExecuteSqlInterpolated` in `EAMS.Infrastructure` or `EAMS.Application`. The
entire port is confined to model configuration and migrations. That is the best case available — do
not undermine it by reaching for raw SQL to solve 3.1.

## Phase 4 — Tests

`backend/EAMS.Tests` splits into `Unit/` (no database) and `Integration/` (real database via
Testcontainers).

- [ ] Switch the Testcontainers image from SQL Server 2022 to PostgreSQL.
- [ ] **Re-prove, do not just re-point.** The integration suite exists specifically to pin filtered
      unique indexes, NULL-equality inside unique indexes, and unique-violation races. Those are
      exactly the behaviours that differ between the two engines.
- [ ] Do **not** move these to EF InMemory. Every property under test vanishes there.
- [ ] Baseline before starting: the SQL Server repo passes **1389** backend and **515** frontend
      tests. Expect the backend count to change — investigate any test that *disappears* rather than
      fails.

## Phase 5 — Railway

Nothing exists yet: **no Dockerfile, no `railway.json`, no `nixpacks.toml`**. All net-new.

The API does **not** serve the SPA (no `UseStaticFiles`, `UseSpa` or `MapFallback` anywhere). They are
two separate apps, as they were two IIS apps. Keep them as two Railway services.

- [ ] **API service** — multi-stage Dockerfile (`sdk:9.0` build → `aspnet:9.0` runtime).
- [ ] Bind to Railway's injected port: `ASPNETCORE_URLS=http://0.0.0.0:${PORT}`. Without this the
      container starts and never receives traffic.
- [ ] **Postgres service** — Railway's managed Postgres.
- [ ] **SPA service** — static bundle behind Caddy, with an SPA fallback rewrite replacing the old IIS
      `httpErrors` rule. Without it, deep links work when clicked and 404 on refresh.
- [ ] **Deploy the API first.** Its domain must exist before the SPA can be built — Vite inlines
      `VITE_API_BASE_URL` at **build** time, and nothing on the server can repoint an existing bundle.
- [ ] Then set `VITE_API_BASE_URL` (include `/api/v1`, no trailing slash) and `VITE_BASE_URL` (`/` if
      the SPA sits at the service root, not `/eams/`) and build.

### Required environment variables

| Variable | Notes |
|---|---|
| `ConnectionStrings__EamsDb` | Double underscore. A single underscore does not bind and the app refuses to boot. |
| `Jwt__SigningKey` | **The host will not start without it.** No default, never generated. 64+ random characters (32-byte HMAC-SHA256 floor, RFC 7518 §3.2). It also rejects anything that looks copied from a sample. |
| `ASPNETCORE_ENVIRONMENT` | Set to **`Production`** — see below. |

### Set `ASPNETCORE_ENVIRONMENT=Production`

Three behaviours are gated on `IsDevelopment()`, and this decides all three at once:

| | `Development` | `Production` |
|---|---|---|
| Swagger UI | yes | no |
| Database seeding | yes | no |
| Well-known dev kiosk key | **seeded** | not seeded |

`Development` seeds a device API key whose value is **public in this repository**
(`SeedData.DevelopmentKioskApiKey`). Railway URLs are public by construction. Use `Production`.

You do not need the seed anyway — Phase 6 brings real data.

## Phase 6 — Data migration (last)

- [ ] Create the schema by running the **new Postgres migrations against an empty database**. Do this
      first, not after loading data.
- [ ] Move data with `pgloader` (it supports a SQL Server source and handles type mapping).
- [ ] **GUIDs as strings, not binary.** SQL Server's `uniqueidentifier` is mixed-endian; a binary copy
      scrambles them.
- [ ] **Reset every sequence** afterwards, or the first insert collides with an existing key.
- [ ] **Do NOT copy `__EFMigrationsHistory`.** The Postgres migrations are new files with new IDs;
      importing the old history makes startup try to re-create existing tables.
- [ ] Regenerate `AttendanceRecord.RowVersion` values from the new sequence — the old `rowversion`
      bytes are meaningless in Postgres. **Preserve relative ordering**, or live-dashboard cursors held
      by existing clients will skip rows.
- [ ] Verify by **row count per table**, not by looking at the UI.

## Phase 7 — Before anyone uses it

- [ ] **This build has no authentication on its endpoints** (ADR-001 D-6; Technical Plan §11 pending).
      Anyone who reaches the API can read every student record and create or delete events — only the
      five device-key capture routes are protected. On a public Railway URL that is real exposure. Put
      Cloudflare Access or equivalent in front until §11 lands.
- [ ] There is **no seeded login credential in the source**, by design. The admin account
      (`dev-admin@usa.edu.ph`) is created only when `Seed__DevelopmentSuperAdminPassword` is set, and
      that is Development-only. With `Production` + migrated data you log in as a user from the
      migrated `Users` table.
- [ ] Password policy minimum is **12 characters**. A shorter seed password is skipped with a warning,
      not an error.

---

## Accept this going in

From the moment the fork is made, the two systems **diverge — code and data both**. Two databases, no
sync. A student added on IIS never appears on Railway, and vice versa.

That is fine if Railway is the future and IIS is winding down. It is a real problem if both are meant
to be live systems people actually use. **Decide which one is real**, because it determines where the
next bug gets fixed.

## Verified facts behind this document

Gathered by inspecting the SQL Server repo at the time of writing. Re-check anything that looks stale.

| Claim | Evidence |
|---|---|
| No raw SQL outside migrations | grep for `FromSqlRaw` / `ExecuteSqlRaw` / interpolated variants: 0 hits |
| 11 filtered indexes | `HasFilter("...")` in `EamsDbContext.cs` |
| 18 unfiltered unique indexes | `HasFilter(null)` in `EamsDbContext.cs` |
| 26 `DateTime`, 0 `DateTimeOffset` | `EAMS.Domain` property declarations |
| 9 migrations, ~22,770 lines | `Migrations/` including snapshot |
| API does not serve the SPA | no `UseStaticFiles` / `UseSpa` / `MapFallback` in `EAMS.Api` |
| No container config exists | no `Dockerfile`, `railway.*`, `nixpacks.*`, `docker-compose*` |
| Auth endpoints | `login`, `refresh`, `logout`, `change-password` — no admin-creates-user route |
| Test baseline | 1389 backend, 515 frontend, both green |
