<#
.SYNOPSIS
    Sets the EAMS API application-pool environment variables on an IIS server, and verifies them.

.DESCRIPTION
    Run this ON THE VM, as Administrator, before deploying a build that includes authentication.

    The API resolves its JWT settings while the host is still being described — before the web
    server is built — so a missing or too-short `Jwt__SigningKey` is a dead site (HTTP 500.30) and
    not a broken login. The connection string is the same kind of failure. Both must exist before the
    new files are copied in, which is why this is its own step rather than something to fix
    afterwards.

    Written as a script because these settings are otherwise four multi-line commands with backtick
    continuations, and those mangle when pasted into a console over RDP.

    Idempotent by default. An existing signing key is left alone, because regenerating one signs out
    every live session. Pass -Force to replace it deliberately.

    This script does NOT deploy anything and does not touch the database. See
    docs/DEPLOYMENT-PREREQUISITES.md for the full order of operations.

.PARAMETER ConnectionString
    The `EamsDb` connection string. Prompted for if omitted, which keeps the SQL password out of your
    shell history.

.PARAMETER AllowedOrigin
    The SPA's origin: scheme and host only, no path and no trailing slash. A browser origin never
    includes a path, so `https://host/eams` silently matches nothing.

.PARAMETER Environment
    ASPNETCORE_ENVIRONMENT. Defaults to Production.

    Development on a reachable host seeds demonstration students and serves Swagger. It does not
    change whether authorization is enforced — that is a separate phase — so choosing Production here
    is about not publishing fixtures and docs, not about locking the API down.

.PARAMETER SigningKey
    Supply an existing key instead of generating one. Use this when rebuilding a server that already
    had sessions you want to keep working, or when several instances must share a key.

.PARAMETER AppPool
    The API's application pool name. Defaults to EamsApi.

.PARAMETER Force
    Replace the signing key even if one is already set. Signs out every live session.

.PARAMETER SkipRestart
    Do not restart the application pool at the end.

.EXAMPLE
    .\vm-setup-env.ps1
    Prompts for the connection string, generates a signing key, sets everything, verifies, restarts.

.EXAMPLE
    .\vm-setup-env.ps1 -AllowedOrigin "https://dev.iloilosupermart.com" -Environment Development
#>

[CmdletBinding()]
param(
    [string]$ConnectionString,
    [string]$AllowedOrigin = 'https://dev.iloilosupermart.com',
    [ValidateSet('Production', 'Staging', 'Development')]
    [string]$Environment = 'Production',
    [string]$SigningKey,
    [string]$AppPool = 'EamsApi',
    [switch]$Force,
    [switch]$SkipRestart
)

$ErrorActionPreference = 'Stop'

$JwtVar   = 'Jwt__SigningKey'
$ConnVar  = 'ConnectionStrings__EamsDb'
$CorsVar  = 'Cors__AllowedOrigins__0'
$EnvVar   = 'ASPNETCORE_ENVIRONMENT'
$KeyBytes = 64   # 512 bits; the host refuses anything under 32 (RFC 7518 section 3.2)

function Write-Step { param($m) Write-Host "  $m" }
function Write-Ok   { param($m) Write-Host "  [ok] $m" -ForegroundColor Green }
function Write-Warn { param($m) Write-Host "  [!]  $m" -ForegroundColor Yellow }
function Write-Head { param($m) Write-Host "`n$m" -ForegroundColor Cyan }

# ------------------------------------------------------------------ preflight

Write-Head 'EAMS — application pool configuration'

$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this in PowerShell as Administrator. Writing applicationHost.config requires it.'
}

try {
    Import-Module WebAdministration -ErrorAction Stop
}
catch {
    throw 'The WebAdministration module is unavailable. Install the IIS management tools (Web-Scripting-Tools).'
}

$poolPath = "IIS:\AppPools\$AppPool"

if (-not (Test-Path $poolPath)) {
    throw "Application pool '$AppPool' does not exist. Create it first — see docs/DEPLOY-IIS.md section 2 — or pass -AppPool."
}

Write-Ok "Application pool '$AppPool' found."

# ------------------------------------------------------------------ helpers

function Get-PoolEnvNames {
    # Returns the names already present, or an empty array. Get-WebConfiguration yields nothing at all
    # when the collection has never been written, which is the normal state on a fresh pool.
    $collection = (Get-WebConfiguration -PSPath $poolPath -Filter 'environmentVariables' -ErrorAction SilentlyContinue).Collection
    if (-not $collection) { return @() }
    return @($collection | ForEach-Object { $_.name })
}

function Set-PoolEnv {
    param([string]$Name, [string]$Value)

    # IIS refuses a duplicate collection entry rather than updating it, so an existing name has to be
    # removed first. Remove-then-add is the only reliable update for this collection.
    if ((Get-PoolEnvNames) -contains $Name) {
        Remove-WebConfigurationProperty -PSPath $poolPath -Filter 'environmentVariables' `
            -Name '.' -AtElement @{name = $Name }
    }

    Set-WebConfigurationProperty -PSPath $poolPath -Filter 'environmentVariables' `
        -Name '.' -Value @{name = $Name; value = $Value }
}

# ------------------------------------------------------------------ gather

$existing = Get-PoolEnvNames

if (-not $ConnectionString) {
    if ($existing -contains $ConnVar) {
        Write-Head '1. Connection string'
        Write-Ok "'$ConnVar' is already set. Leaving it alone (pass -ConnectionString to replace)."
    }
    else {
        Write-Head '1. Connection string'
        Write-Step 'Example:'
        Write-Step '  Server=.;Database=EAMS;User Id=eams_app;Password=...;TrustServerCertificate=True'
        $ConnectionString = Read-Host 'Enter the EamsDb connection string'
        if (-not $ConnectionString) { throw 'A connection string is required.' }
    }
}
else {
    Write-Head '1. Connection string'
}

if ($ConnectionString) {
    Set-PoolEnv $ConnVar $ConnectionString
    Write-Ok "Set '$ConnVar'."
}

# ------------------------------------------------------------------ signing key

Write-Head '2. JWT signing key'

$generated = $null

if (($existing -contains $JwtVar) -and -not $Force -and -not $SigningKey) {
    Write-Ok "'$JwtVar' is already set. Leaving it alone."
    Write-Step 'Regenerating would sign out every live session — pass -Force if that is what you want.'
}
else {
    if ($SigningKey) {
        if ($SigningKey.Length -lt 32) {
            throw 'The supplied signing key is shorter than 32 characters. The host will refuse to start.'
        }
        $generated = $SigningKey
        Write-Ok 'Using the supplied signing key.'
    }
    else {
        $bytes = New-Object byte[] $KeyBytes
        # RandomNumberGenerator, not Get-Random — the latter is a seeded PRNG and must never produce
        # a signing key.
        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
        $generated = [Convert]::ToBase64String($bytes)
        Write-Ok "Generated a new key ($KeyBytes random bytes)."
    }

    if ($existing -contains $JwtVar) { Write-Warn 'Replacing the existing key — every live session is now invalid.' }

    Set-PoolEnv $JwtVar $generated
    Write-Ok "Set '$JwtVar'."
}

# ------------------------------------------------------------------ cors + environment

Write-Head '3. CORS origin and environment'

if ($AllowedOrigin -match '/[^/]' -and $AllowedOrigin -notmatch '^https?://[^/]+/?$') {
    Write-Warn "'$AllowedOrigin' looks like it contains a path. A browser origin is scheme + host only,"
    Write-Warn 'and one with a path silently matches nothing. Continuing anyway.'
}

Set-PoolEnv $CorsVar $AllowedOrigin.TrimEnd('/')
Write-Ok "Set '$CorsVar' to $($AllowedOrigin.TrimEnd('/'))."

Set-PoolEnv $EnvVar $Environment
Write-Ok "Set '$EnvVar' to $Environment."

if ($Environment -eq 'Development') {
    Write-Warn 'Development on a reachable host seeds demonstration students and serves Swagger.'
    Write-Warn 'It does not enforce authorization either way — that is a separate phase.'
}

# ------------------------------------------------------------------ verify

Write-Head '4. Verify'

$final = Get-PoolEnvNames
$wanted = @($ConnVar, $JwtVar, $CorsVar, $EnvVar)
$missing = @($wanted | Where-Object { $final -notcontains $_ })

foreach ($name in $wanted) {
    if ($final -contains $name) { Write-Ok $name } else { Write-Warn "$name — MISSING" }
}

if ($missing.Count -gt 0) {
    Write-Host ''
    throw "Not all variables were written: $($missing -join ', '). If this server predates IIS 10 it does not support application-pool environment variables — use an <environmentVariables> block in the API's web.config instead."
}

# ------------------------------------------------------------------ restart

if (-not $SkipRestart) {
    Write-Head '5. Restart'
    Restart-WebAppPool -Name $AppPool
    Write-Ok "Restarted '$AppPool'."
}

# ------------------------------------------------------------------ summary

if ($generated) {
    Write-Head 'SIGNING KEY — copy this now, it is not shown again'
    Write-Host ''
    Write-Host "    $generated"
    Write-Host ''
    Write-Step 'You need this exact value to run create-admin in the next step, because that command'
    Write-Step 'runs outside the application pool and cannot see the pool variables.'
}

Write-Head 'Next: create the administrator'
Write-Host ''
Write-Step 'Nothing creates one for you. From the published API directory:'
Write-Host ''
Write-Host '    cd C:\inetpub\eams\api'
Write-Host '    $env:ConnectionStrings__EamsDb = "<the connection string above>"'
Write-Host '    $env:Jwt__SigningKey            = "<the key above>"'
Write-Host '    dotnet EAMS.Api.dll create-admin --email admin@usa.edu.ph --name "Full Name" --role SuperAdmin'
Write-Host ''
Write-Step 'Both $env: lines are required. Application-pool variables belong to the IIS worker'
Write-Step 'process, and create-admin runs on the fully built host — so it refuses to start without'
Write-Step 'a signing key even though it never mints a token.'
Write-Host ''
