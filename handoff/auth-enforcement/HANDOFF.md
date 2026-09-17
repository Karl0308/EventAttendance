# Handoff: enforce authorization on every API route (SQL Server / IIS repo)

**Source:** `PatrickPo87/E1-USA-ATTENDANCE` (the Railway / PostgreSQL copy of this app), commit `5f3395a`,
2026-09-17
**Target:** this repository, `Karl0308/EventAttendance` (SQL Server + IIS)
**Verified against:** branch `feat/backend-phases-0-3a` at `1b41a5c`. `git apply --check` is clean for
both patches in this folder.

The two repositories are the same application: same controllers, same auth classes, same tests. Only
hosting and database differ. The change contains **no schema change, no migration and nothing
provider-specific**, so it ports as a patch.

---

## 1. What the change does

Before, only the five device-key capture routes were protected. Anyone who could reach `/eamsapi` could
read every student, create and delete events, and mint device keys. `docs/DEPLOY-IIS.md` §0 says so.

After:

| Caller | Routes | Without a valid credential | Credential lacks the permission |
|---|---|---|---|
| Nobody | `POST /auth/login`, `POST /auth/refresh` | open | — |
| Admin SPA user (`Authorization: Bearer …`) | Every other admin route, each needing its §4.11 permission (`students.read`, `devices.write`, …) | `401` | `403` |
| Device (`Authorization: DeviceKey …`) | tap, tap/batch, by-card, heartbeat, manifest (unchanged) | `401` | `403` (revoked or inactive) |
| Either | `GET /events` — a user needs `events.read`; a device gets **its school's `Open` events only** | `401` | `403` |

Consequences to know about:

- **`POST /devices` and `POST /devices/{id}/regenerate-key` need `devices.write`.** A device can no
  longer enrol itself; an admin creates it on the SPA's Devices page. The key dialog now also shows
  the **device ID**, which a handset needs for heartbeat.
- **Devices already holding a key are unaffected**, including the mobile app's event picker on
  `GET /events`.
- **Gated routes take their tenant from the token's `school_id`**, not from the development pin (the
  lowest school `Code`). With one school this changes nothing visible.
- **The "AUTHORIZATION IS NOT ENFORCED" startup warning is gone**, together with
  `[assembly: AuthorizationNotEnforced]` and `AuthorizationStatus`.
- **The SPA needs no functional change.** It already signs in, sends the Bearer token on every call,
  renews on `401`, and checks the same permission codes per page.

## 2. How it is built

Read these before resolving anything by hand:

- **Controllers.** Every admin action carries
  `[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.X)]`
  directly above the existing `[HasPermissionNotEnforced(EamsPermissions.X)]`. That second attribute
  still enforces nothing; it records the code for `PermissionRegistryTests`.
- **`Program.cs`** registers one Bearer policy per code in `EamsRoles.HumanAssignable`. An `[Authorize]`
  naming an unregistered policy is a **500 on every request**, not a 403.
- **`Authentication/DeviceKeyOrBearer.cs`** (new) is a forwarding scheme used only by `GET /events`. It
  picks the DeviceKey or the Bearer handler from the `Authorization` header. Naming both schemes on one
  `[Authorize]` instead makes both handlers write a 401 body into the same response.
- **Guard tests:**
  - `Unit/AuthorizationCoverageTests.cs` fails the build for any action without a gate, with the wrong
    scheme, or with a policy different from its declared permission. It replaces
    `AuthorizationSeamTests.cs`.
  - `Integration/AuthEnforcementTests.cs` sends an anonymous request to **every route in the host's
    route table** and expects `401`. It replaces `AuthStagedCutoverTests.cs`.
- **Test helper.** `IntegrationTest.SignedInClientAsync(factory[, schoolId][, role])` creates a user,
  signs in through `POST /auth/login` and returns a client carrying the token. Without `schoolId` it
  signs in to the school the development pin would have chosen, so existing tenancy assertions stay
  valid. About twenty integration test files now use it.

## 3. Apply it

Run from the repo root, in PowerShell.

```powershell
# 0. Baseline FIRST, on the untouched branch. Some tests may already fail on this machine,
#    and you need to know which before judging the result.
dotnet test EAMS.sln -c Release *> baseline-test.log

# 1. Branch
git checkout -b feat/enforce-authorization

# 2. Code: API, SPA, tests, generator script (54 files)
git apply --ignore-whitespace handoff/auth-enforcement/code.patch

# 3. The one file the patch cannot delete (its first line differs from the source repo).
#    AuthEnforcementTests.cs, added by the patch, replaces it.
git rm backend/EAMS.Tests/Integration/AuthStagedCutoverTests.cs

# 4. Docs: mobile handoff, changelog, testing guide, QA overview, ADR index (7 files)
git apply --ignore-whitespace handoff/auth-enforcement/docs.patch

# 5. Regenerate the published contract and the endpoint index. They are generated,
#    so do not copy them from the other repo.
$env:EAMS_UPDATE_OPENAPI = "1"
dotnet test EAMS.sln -c Release --filter The_committed_contract_is_the_generated_one
Remove-Item Env:EAMS_UPDATE_OPENAPI
node scripts/generate-endpoint-index.mjs --write
```

### 3a. Manual doc edits (not in the patch — these files differ between the repos)

**`CLAUDE.md`** — replace the bullet
`**Auth is deliberately stubbed** — endpoints are open. Don't "fix" this incidentally; …` with:

```markdown
- **Every endpoint except `POST /auth/login` and `/auth/refresh` is gated** (plan §11). Admin routes
  carry `[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy =
  EamsPermissions.X)]` beside `[HasPermissionNotEnforced(EamsPermissions.X)]` — the second records the
  code for the registry tests and enforces nothing on its own. The five capture routes (tap, tap/batch,
  by-card, heartbeat, manifest) take a `DeviceKey` instead. `GET /events` is the one shared route — the
  capture app's event picker — on the `DeviceKeyOrBearer` forwarding scheme, and a device gets `Open`
  events only. A new action needs the pair, or `AuthorizationCoverageTests` fails. Gated routes resolve
  their tenant from the token's `school_id`, not the development pin — so an integration test calling
  one signs in with `SignedInClientAsync`.
```

**`docs/DEPLOY-IIS.md`** has three passages that become false:

- **§0, "Access — this build has no authentication".** Replace it with: every endpoint except sign-in
  demands a credential; the first administrator is the way in; devices are enrolled from the admin UI.
  An IP allow-list is still a sensible second layer.
- **The note under §0** ("§11 is now partly built … No endpoint enforces any of it yet"). Delete it.
- **Under "Creating the first administrator"** ("Until §11 enforcement lands there is nothing to log in
  *to*…"). Replace it with: **create this account before deploying the enforcing build**, or nobody can
  use the admin UI.

### 3b. Verify

The same checks CI runs:

```powershell
dotnet build EAMS.sln -c Release
dotnet test EAMS.sln -c Release          # Testcontainers.MsSql: Docker must be running
cd web-admin; npm run lint; npm run build
```

Compare the test failures with `baseline-test.log`. **Nothing new should fail.** In the source repo the
final run was 1,758 passed and 6 failed: 5 failed identically before the change, and the sixth was the
contract regenerated in step 5.

## 4. SQL Server — nothing to change, two things to know

- **No migration.** The change is attributes, one authentication scheme, policies and tests.
- **Baseline failures will differ** from the PostgreSQL repo. Its five (refresh-token and term-admin
  races, pagination and tap-batch ordering, device revoke) were specific to that setup. Judge only
  against your own baseline from step 0.
- `SignedInClientAsync` chooses the school with `OrderBy(s => s.Code)`, exactly as the development pin
  does. Both use SQL Server's collation here, so they agree.

## 5. IIS — check before deploying

1. **Anonymous Authentication must stay Enabled, and Windows Authentication Disabled, on `/eamsapi`**
   (IIS Manager → the `eamsapi` application → Authentication). With Windows Authentication on, IIS
   answers `401` itself and ASP.NET Core never sees the `Bearer` or `DeviceKey` header. The symptom is
   every request failing, including correct ones.
2. **Create the first administrator before the deploy** — `docs/DEPLOY-IIS.md` →
   *Creating the first administrator* (`dotnet EAMS.Api.dll create-admin …`). If users already exist,
   confirm at least one holds `SchoolAdmin` or `SuperAdmin`.
3. **Same origin is already in place.** The SPA (`/eams`) and API (`/eamsapi`) share
   `dev.iloilosupermart.com`, so sign-in and the refresh cookie work as they do today. Nothing in this
   change touches that.
4. **Existing kiosks and handsets keep working.** New ones are registered on the Devices page, and the
   dialog shows the key and device ID once.

### Smoke test after deploying

```powershell
$api = "https://dev.iloilosupermart.com/eamsapi/api/v1"
curl.exe -i "$api/students"                                              # 401
curl.exe -i -X POST "$api/devices" -H "Content-Type: application/json" -d "{}"   # 401
curl.exe -H "Authorization: DeviceKey <an existing key>" "$api/events"   # 200, Open events only
```

Then sign in to `https://dev.iloilosupermart.com/eams/` and open Students, Events and Devices.

## 6. Not ported — only relevant to the Railway repo

These were excluded from the patch on purpose; they have no counterpart here:

- `RailwayBootstrap.cs` (first-boot admin)
- `Caddyfile`, `Dockerfile.*`
- `docs/RAILWAY-*.md`

## 7. Mobile app

Nothing changes for a device that already has its key. `docs/api/mobile-auth-handoff.md`, added by the
docs patch, is the note for the mobile developer. The one visible difference for them is that a new
device gets its key and device ID from an admin instead of enrolling itself.

## 8. Rollback

No schema change, so rolling back is redeploying the previous binaries and SPA bundle. Users, roles
and devices created in the meantime stay valid for the older build.

## 9. Lessons from doing this the first time

- **Keep `AuthEnforcementTests.Every_route_outside_sign_in_refuses_a_request_with_no_credential`
  anonymous** (`factory.CreateClient()`). A bulk "sign the failing tests in" edit once gave it a token,
  and every route then *appeared* open.
- **Regenerate `docs/api/openapi.json` after any XML-comment change on a controller**, including class
  summaries, which are published as tag descriptions. Otherwise
  `The_committed_contract_is_the_generated_one` fails.
- **A test that calls a gated route with no school in the database fails inside
  `SignedInClientAsync`** ("Sequence contains no elements"). Add a school first.
