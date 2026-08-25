# Deploy runbook

Two commands. One on your machine, one on the server.

Read [`DEPLOYMENT-PREREQUISITES.md`](DEPLOYMENT-PREREQUISITES.md) **before your first deploy** - it
covers the settings whose absence stops the host from starting. This page is what you run every time
after that.

---

## 1. On your machine

```powershell
cd "D:\Personal Apps\USA-Attendance"
git pull
.\scripts\build-release.ps1
```

That builds the API and the SPA and stages everything into `release\`:

```
release\
├── api\            the published API
├── web\            the built SPA
└── vm-deploy.ps1   the script you run on the server
```

**Check these two lines before going further.** They are the difference between a deploy and an
afternoon:

```
[ok] SPA built; assets are under '/eams/assets/'
[ok] API base URL '/eamsapi/api/v1' is baked into index-*.js
```

The script refuses to stage a bundle that fails either, because both have shipped broken before and
neither fails loudly on the server - the first is a blank white page, the second is every API call
404ing against a prefix nothing is mounted on. See §5.

## 2. Copy to the server

```
D:\Personal Apps\USA-Attendance\release\   ->   C:\release\
```

All three: `api\`, `web\`, and `vm-deploy.ps1`. **Overwrite everything, including the script** - a
stale copy of `vm-deploy.ps1` once emptied an application folder, and another once deployed a build
from the previous day while reporting success.

Anywhere outside `C:\inetpub` works. `C:\release` is the convention here.

## 3. On the server

```powershell
cd C:\release
.\vm-deploy.ps1
```

It backs up what is there, takes the API offline with `app_offline.htm`, copies both applications,
restores the configuration, brings it back up, and checks that it answers.

**No administrator rights are needed.** Stopping an application pool requires them; `app_offline.htm`
does not - the module sees the file appear and releases the DLLs.

### Read four lines of its output

```
API  -> C:\inetpub\wwwroot\eamsapi   (from IIS /eamsapi)
API build: 08/25/2026 19:22
[ok] Connection string carried forward.
[ok] Signing key carried forward - existing sessions survive this deploy.
```

| Line | What it is telling you |
|---|---|
| `(from IIS /eamsapi)` | The path was read from IIS. `(DEFAULT - not confirmed against IIS)` means it is a guess - stop, run `Get-WebApplication \| Select-Object path, PhysicalPath`, and pass `-ApiPath` |
| `API build:` | **Today's date.** An older one means the copy in step 2 did not land |
| `carried forward` | Your connection string and signing key survived. A publish overwrites `web.config`, and the script re-applies what was there - which is also why nobody has to retype a signing key, and why nobody gets signed out |

The first two have each been wrong on a real deploy, and both times every other line still said `[ok]`.

## 4. Verify

```powershell
# The API answers
(Invoke-WebRequest "http://localhost/eamsapi/api/v1/events" -UseBasicParsing).StatusCode

# The SPA, and a deep link - the second is the web.config rewrite
(Invoke-WebRequest "http://localhost/eams/" -UseBasicParsing).StatusCode
(Invoke-WebRequest "http://localhost/eams/login" -UseBasicParsing).StatusCode

# PUT and DELETE reach the application. 404 = correct. 405 = IIS WebDAV is blocking them (§5)
try { (Invoke-WebRequest "http://localhost/eamsapi/api/v1/events/11111111-1111-1111-1111-111111111111" -Method DELETE -UseBasicParsing).StatusCode } catch { $_.Exception.Response.StatusCode.value__ }
```

There is no `curl.exe` on Windows Server 2016. Use `Invoke-WebRequest`.

Then, in a browser: **hard-refresh** (`Ctrl+F5`, the bundle filename changes every build), sign in, and
exercise one write - editing a student will do.

## First deploy only

Create an administrator. Nothing does it for you, and there is no default login.

```powershell
cd C:\inetpub\wwwroot\eamsapi
([xml](Get-Content .\web.config)).SelectNodes('//environmentVariable') |
  ForEach-Object { Set-Item -Path "env:$($_.name)" -Value $_.value }
dotnet EAMS.Api.dll create-admin --email admin@usa.edu.ph --name "Full Name" --role SuperAdmin
```

`create-admin` runs outside IIS, so it sees neither app-pool variables nor `web.config`. The first
command reads them out of the live config into the shell, which avoids retyping a signing key - a
mistyped one signs out every live session.

It refuses an address that already exists rather than resetting it, so re-running it is harmless.

---

## 5. Things that have actually gone wrong here

Each of these cost real time, and each looked like something else first.

### 405 on every PUT and DELETE

**IIS ships WebDAV enabled**, and its module claims `PUT` and `DELETE` before the ASP.NET Core Module
sees them. The request never reaches the application: no log line, no exception, no trace id.

Eleven routes were affected - editing a student or event, deleting either, removing a card, changing
the current term - and it survived deployment because Kestrel has no WebDAV, so nothing reproduces
locally and no test can catch it.

`backend/EAMS.Api/web.config` removes both the module and the handler. If it recurs, that block was
lost in a deploy.

### A blank white page

`web-admin\.env.production` is git-ignored, so it does not arrive with a clone. Without it Vite emits
assets at `/` instead of `/eams/`. `build-release.ps1` writes the file and then refuses to stage a
bundle whose `index.html` disagrees.

### Every API call 404s while the page loads

Same file, different line. A **UTF-8 BOM** on line 1 makes Vite read the first key as
`<BOM>VITE_API_BASE_URL`, which does not begin with `VITE_` and is silently ignored - so the asset
path is right and the API base is the default. `Set-Content -Encoding utf8` writes that BOM on
Windows PowerShell 5.1. The script now writes the file without one and greps the built bundle for the
expected base URL.

### Deep links 404 while the app loads

The SPA's `web.config` needs `responseMode="ExecuteURL"` with a **site-relative** path. `File` with a
relative path resolves from the site root, not the application, so an app at `/eams` never finds it;
an absolute path needs `allowAbsolutePathsWhenDelegated` in `applicationHost.config` and answers
`500` without it. `build-release.ps1` derives the path from `VITE_BASE_URL` so it cannot drift.

### A script that closes instantly, or will not parse

Windows PowerShell 5.1 reads a `.ps1` without a byte-order mark as **Windows-1252**, so a UTF-8 em
dash becomes three characters ending in a curly quote - which PowerShell accepts as a string
delimiter, ending the string mid-sentence. **Write every `.ps1` here ASCII-only with a BOM**, and
check it parses both ways.

A file copied from another machine is also blocked by default: `Unblock-File .\vm-deploy.ps1`.

### Deploying into a folder nobody serves

The documented layout (`C:\inetpub\eams\api`) is not the deployed one (`C:\inetpub\wwwroot\eamsapi`).
A deploy to the wrong folder succeeds at every step and leaves the old build running - the only
evidence was a route still answering 404 afterwards. `vm-deploy.ps1` now asks IIS instead of guessing.

---

## Rolling back

```powershell
Stop-WebAppPool -Name "EamsApi"     # or drop app_offline.htm into the api folder
# copy C:\backup\eams\<timestamp>\api\* and \web\* back over the application folders
Start-WebAppPool -Name "EamsApi"
```

`vm-deploy.ps1` takes that backup before touching anything and prints the path.

**The database is not rolled back by this.** Migrations apply on startup, so take a backup before
deploying a build that is more than a few commits ahead of what is running. Migrations here are
additive, so the previous build generally runs against the newer schema.

---

## Related

- [`DEPLOYMENT-PREREQUISITES.md`](DEPLOYMENT-PREREQUISITES.md) - what must be true before the first deploy
- [`DEPLOY-IIS.md`](DEPLOY-IIS.md) - first-time IIS setup: app pools, applications, the hosting bundle
- [`api/mobile-changes.md`](api/mobile-changes.md) - what changed for the mobile client
