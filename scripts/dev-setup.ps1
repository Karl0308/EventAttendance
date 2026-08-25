<#
.SYNOPSIS
    One-command local setup for EAMS. Generates the two secrets the API needs and prints the login.

.DESCRIPTION
    The API refuses to start without `Jwt:SigningKey`, and it seeds no administrator without
    `Seed:DevelopmentSuperAdminPassword`. Both are deliberately absent from the repository — the
    signing key because a shared one is not a secret, the password because a working administrator
    credential in a public repository is a working administrator credential on the internet.

    That is correct and it is also two commands and a doc page between `git clone` and a usable
    application. This script is the answer to that: it generates both values, stores them in .NET
    user secrets (per-machine, per-user, outside the repository and outside every diff), and tells
    you what to sign in with.

    Safe to re-run. Existing secrets are left alone unless -Force is given, because regenerating
    them is not the harmless act it looks like — see the note on -Force below.

.PARAMETER Force
    Overwrite secrets that already exist.

    Two consequences worth knowing before you use it. Regenerating the signing key invalidates every
    session that is currently live, so anyone signed in is signed out. And regenerating the seed
    password does NOT change the password of an administrator that already exists: the seed creates
    the account only if it is absent, so the new value is silently unused until the database is
    dropped and recreated.

.PARAMETER ShowSecrets
    Print the signing key as well as the password. Off by default so the key does not end up in a
    screen share or a scrollback buffer for no reason.

.PARAMETER RemoveSeedPassword
    Remove `Seed:DevelopmentSuperAdminPassword` and exit, leaving the signing key in place.

    Run this before a full `dotnet test`. RbacSeedTests asserts that a Development host with no seed
    password creates no administrator; it clears the environment variable but cannot clear a user
    secret, so having one configured turns that test red locally while CI — which has no user
    secrets — stays green. An administrator that has already been seeded keeps working afterwards,
    because the password is stored hashed in the database and this setting is only read at creation.

.EXAMPLE
    .\scripts\dev-setup.ps1
    Generate what is missing and print the login.

.EXAMPLE
    .\scripts\dev-setup.ps1 -RemoveSeedPassword
    Clear the seed password before running the full test suite.
#>

[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$ShowSecrets,
    [switch]$RemoveSeedPassword
)

$ErrorActionPreference = 'Stop'

# Resolved from this script's own location rather than the caller's working directory, so it behaves
# the same run from the repository root, from scripts/, or by absolute path from anywhere.
$repoRoot  = Split-Path -Parent $PSScriptRoot
$apiProject = Join-Path $repoRoot 'backend\EAMS.Api'

$JwtKeyName      = 'Jwt:SigningKey'
$SeedKeyName     = 'Seed:DevelopmentSuperAdminPassword'
$AdminEmail      = 'dev-admin@usa.edu.ph'
$SigningKeyBytes = 64   # 512 bits, well above HMAC-SHA256's 32-byte floor (RFC 7518 section 3.2)
$PasswordBytes   = 12   # 16 base64 characters, above the 12-character minimum the policy enforces

function Write-Step   { param($m) Write-Host "  $m" }
function Write-Ok     { param($m) Write-Host "  [ok] $m"   -ForegroundColor Green }
function Write-Warn   { param($m) Write-Host "  [!]  $m"   -ForegroundColor Yellow }
function Write-Head   { param($m) Write-Host "`n$m" -ForegroundColor Cyan }

function New-RandomBase64 {
    param([int]$ByteCount)
    $bytes = New-Object byte[] $ByteCount
    # RandomNumberGenerator, not Get-Random: the latter is a seeded PRNG and is not suitable for a
    # signing key. Created via ::Create() so this works on Windows PowerShell 5.1 as well as 7+.
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try   { $rng.GetBytes($bytes) }
    finally { $rng.Dispose() }
    [Convert]::ToBase64String($bytes)
}

function Get-ExistingSecret {
    param([string]$Name)
    # `user-secrets list` prints `key = value`, one per line, or a "No secrets" sentence. Split on
    # the FIRST '=' only: a base64 value can legitimately end in '=' padding.
    $listed = & dotnet user-secrets list --project $apiProject 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $listed) { return $null }
    foreach ($line in $listed) {
        $split = $line.IndexOf('=')
        if ($split -lt 1) { continue }
        if ($line.Substring(0, $split).Trim() -eq $Name) {
            return $line.Substring($split + 1).Trim()
        }
    }
    return $null
}

function Set-Secret {
    param([string]$Name, [string]$Value)
    & dotnet user-secrets set $Name $Value --project $apiProject | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to set '$Name'." }
}

Write-Head 'EAMS local setup'

if (-not (Test-Path $apiProject)) {
    throw "Could not find $apiProject. Run this from inside the repository."
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK is not on PATH. Install .NET 9 and try again.'
}

# ------------------------------------------------------------------ remove-and-exit mode

if ($RemoveSeedPassword) {
    & dotnet user-secrets remove $SeedKeyName --project $apiProject 2>$null | Out-Null
    Write-Ok "Removed '$SeedKeyName'."
    Write-Step 'The full test suite will now pass. An administrator that already exists still works —'
    Write-Step 'the password is stored hashed in the database, and this setting is read only at creation.'
    Write-Host ''
    exit 0
}

# ------------------------------------------------------------------ signing key

Write-Head '1. JWT signing key'

$existingJwt = Get-ExistingSecret $JwtKeyName

if ($existingJwt -and -not $Force) {
    Write-Ok "'$JwtKeyName' is already set. Leaving it alone (-Force to regenerate)."
}
else {
    if ($existingJwt) { Write-Warn 'Regenerating — every live session is now invalid.' }
    Set-Secret $JwtKeyName (New-RandomBase64 $SigningKeyBytes)
    Write-Ok "Generated and stored '$JwtKeyName' ($SigningKeyBytes random bytes)."
}

# ------------------------------------------------------------------ seed password

Write-Head '2. Development administrator'

$existingSeed = Get-ExistingSecret $SeedKeyName
$password     = $existingSeed

if ($existingSeed -and -not $Force) {
    Write-Ok "'$SeedKeyName' is already set. Leaving it alone (-Force to regenerate)."
}
else {
    if ($existingSeed) {
        Write-Warn 'Regenerating. If the administrator already exists in your database this new'
        Write-Warn 'password will NOT apply — the seed only creates an account that is absent.'
    }
    $password = New-RandomBase64 $PasswordBytes
    Set-Secret $SeedKeyName $password
    Write-Ok "Generated and stored '$SeedKeyName'."
}

# ------------------------------------------------------------------ summary

Write-Head 'Sign in with'
Write-Host ''
Write-Host "    Email     $AdminEmail"
Write-Host "    Password  $password"
Write-Host ''
Write-Step 'Stored in user secrets, so you can always read it back with:'
Write-Step "    dotnet user-secrets list --project backend\EAMS.Api"

if ($existingSeed -and -not $Force) {
    Write-Host ''
    Write-Step 'That is the value already configured, not a new one.'
}
else {
    Write-Host ''
    Write-Warn 'This applies only if the administrator does not exist in your database yet.'
    Write-Warn 'The seed creates the account when it is absent and does nothing when it is not, so'
    Write-Warn 'on a database you have already run against, the earlier password is still the one'
    Write-Warn 'that works. Drop the database and let it re-seed to adopt this value.'
}

if ($ShowSecrets) {
    Write-Head 'Signing key'
    Write-Host ''
    Write-Host "    $(Get-ExistingSecret $JwtKeyName)"
    Write-Host ''
}

Write-Head 'Next'
Write-Step 'cd backend\EAMS.Api;  dotnet run --urls "http://localhost:5080"'
Write-Step 'cd web-admin;         npm install;  npm run dev'
Write-Host ''
Write-Step 'The account is created on the next start of a Development host, and only if it is absent.'
Write-Step 'Watch the log for "Seeded the Development SuperAdmin" — a seed that fails warns and lets'
Write-Step 'the host start, so a clean boot is not by itself evidence that an account exists.'

Write-Head 'Before running the full test suite'
Write-Step '.\scripts\dev-setup.ps1 -RemoveSeedPassword'
Write-Step 'RbacSeedTests asserts that a Development host with no seed password creates no'
Write-Step 'administrator. It clears the environment variable but cannot clear a user secret, so'
Write-Step 'leaving one configured turns that test red locally while CI stays green.'
Write-Host ''
