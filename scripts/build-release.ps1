<#
.SYNOPSIS
    Builds the API and the SPA and stages everything the VM needs into one folder.

.DESCRIPTION
    Run this on YOUR machine. It produces `release\` containing:

        release\api\           the published API
        release\web\           the built SPA
        release\vm-deploy.ps1  the script to run on the server

    Copy that whole folder to the VM and run vm-deploy.ps1 there. That is the entire deployment.

    The one thing this exists to prevent is a bundle built without web-admin\.env.production. That
    file is git-ignored, so it does not arrive with a clone, and without it Vite emits assets at `/`
    instead of `/eams/` and the SPA calls `/api/v1` instead of `/eamsapi/api/v1`. The result is a
    blank white page and 404s, with nothing in any log naming the cause. This script writes the file
    if it is missing and refuses to stage a bundle that came out wrong.

.PARAMETER ApiBaseUrl
    Where the SPA sends requests. Must match the API's IIS virtual path.

.PARAMETER BaseUrl
    The URL prefix the SPA is served from. Must match the SPA's IIS virtual path, with the trailing
    slash.

.PARAMETER OutputPath
    Where to stage. Defaults to `release` beside the repository root.

.PARAMETER SkipInstall
    Skip `npm ci`. Faster when node_modules is already current.

.EXAMPLE
    .\scripts\build-release.ps1
#>

[CmdletBinding()]
param(
    [string]$ApiBaseUrl = '/eamsapi/api/v1',
    [string]$BaseUrl = '/eams/',
    [string]$OutputPath,
    [switch]$SkipInstall
)

$ErrorActionPreference = 'Stop'

function Write-Step { param($m) Write-Host "  $m" }
function Write-Ok   { param($m) Write-Host "  [ok] $m" -ForegroundColor Green }
function Write-Warn { param($m) Write-Host "  [!]  $m" -ForegroundColor Yellow }
function Write-Head { param($m) Write-Host "`n$m" -ForegroundColor Cyan }

$repoRoot = Split-Path -Parent $PSScriptRoot
$webAdmin = Join-Path $repoRoot 'web-admin'
$apiProj  = Join-Path $repoRoot 'backend\EAMS.Api\EAMS.Api.csproj'
if (-not $OutputPath) { $OutputPath = Join-Path $repoRoot 'release' }

$apiOut = Join-Path $OutputPath 'api'
$webOut = Join-Path $OutputPath 'web'

try {

Write-Head 'EAMS - build a release'

foreach ($required in @($apiProj, $webAdmin)) {
    if (-not (Test-Path $required)) { throw "Not found: $required. Run this from inside the repository." }
}
foreach ($tool in @('dotnet', 'npm')) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "'$tool' is not on PATH." }
}

Write-Ok "Repository: $repoRoot"

# ------------------------------------------------------------------ 1. .env.production

Write-Head '1. SPA build configuration'

$envFile = Join-Path $webAdmin '.env.production'
$wanted  = "VITE_API_BASE_URL=$ApiBaseUrl`nVITE_BASE_URL=$BaseUrl`n"

if (Test-Path $envFile) {
    $current = Get-Content $envFile -Raw
    if ($current -match [regex]::Escape("VITE_API_BASE_URL=$ApiBaseUrl") -and
        $current -match [regex]::Escape("VITE_BASE_URL=$BaseUrl")) {
        Write-Ok '.env.production already has the expected values.'
        $firstBytes = [System.IO.File]::ReadAllBytes($envFile) | Select-Object -First 3
        if ($firstBytes.Count -ge 3 -and $firstBytes[0] -eq 0xEF -and $firstBytes[1] -eq 0xBB -and $firstBytes[2] -eq 0xBF) {
            Write-Warn 'It starts with a byte-order mark, so Vite will ignore the FIRST line. Rewriting without one.'
            [System.IO.File]::WriteAllText($envFile, $wanted, (New-Object System.Text.UTF8Encoding($false)))
            Write-Ok 'Rewritten without a BOM.'
        }
    }
    else {
        Write-Warn '.env.production exists but does not match the requested values:'
        ($current -split "`n" | Where-Object { $_ -match '\S' }) | ForEach-Object { Write-Step "  $_" }
        Write-Warn "Expected VITE_API_BASE_URL=$ApiBaseUrl and VITE_BASE_URL=$BaseUrl"
        Write-Warn 'Leaving it alone - delete it and re-run if the generated values are what you want.'
    }
}
else {
    # WriteAllText with an explicit BOM-less encoding, NOT Set-Content -Encoding utf8: on Windows
    # PowerShell 5.1 that switch writes a byte-order mark, and Vite then reads the first key as
    # "<BOM>VITE_API_BASE_URL", which does not start with VITE_ and is silently ignored. The second
    # line still parses, so the build half-works - correct asset paths, default API URL - and the
    # only symptom is every API call 404ing against a prefix nothing is mounted on.
    [System.IO.File]::WriteAllText($envFile, $wanted, (New-Object System.Text.UTF8Encoding($false)))
    Write-Ok "Created .env.production (git-ignored, so it does not arrive with a clone)."
    Write-Step "  VITE_API_BASE_URL=$ApiBaseUrl"
    Write-Step "  VITE_BASE_URL=$BaseUrl"
}

# ------------------------------------------------------------------ 2. clean output

Write-Head '2. Staging folder'

if (Test-Path $OutputPath) {
    Remove-Item $OutputPath -Recurse -Force
    Write-Ok 'Cleared the previous release.'
}
[void](New-Item -ItemType Directory -Path $OutputPath -Force)
Write-Ok $OutputPath

# ------------------------------------------------------------------ 3. API

Write-Head '3. Publishing the API'

& dotnet publish $apiProj -c Release -o $apiOut --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

if (-not (Test-Path (Join-Path $apiOut 'EAMS.Api.dll'))) { throw "EAMS.Api.dll is missing from $apiOut." }
Write-Ok 'API published.'

# ------------------------------------------------------------------ 4. SPA

Write-Head '4. Building the SPA'

Push-Location $webAdmin
try {
    if (-not $SkipInstall) {
        & npm ci
        if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' }
    }
    & npm run build
    if ($LASTEXITCODE -ne 0) { throw 'npm run build failed.' }
}
finally { Pop-Location }

$dist = Join-Path $webAdmin 'dist'
if (-not (Test-Path (Join-Path $dist 'index.html'))) { throw "index.html is missing from $dist." }

# The check this script exists for. A bundle whose assets are not under the SPA's virtual path
# produces a blank page on the server and nothing that names the cause.
$indexHtml = Get-Content (Join-Path $dist 'index.html') -Raw
$expected  = ($BaseUrl.TrimEnd('/')) + '/assets/'
if ($indexHtml -notmatch [regex]::Escape($expected)) {
    throw "index.html does not reference '$expected'. .env.production was not picked up - the SPA would serve a blank page. Delete web-admin\.env.production, re-run, and check the values above."
}
Write-Ok "SPA built; assets are under '$expected'."

# Checking index.html only proves VITE_BASE_URL was read. VITE_API_BASE_URL is inlined into the
# JavaScript instead, and it failed independently once: the asset path was right, the API base was
# the default, and every request went to a prefix with no application on it. Check the bundle.
$bundle = Get-ChildItem (Join-Path $dist 'assets') -Filter '*.js' | Sort-Object Length -Descending | Select-Object -First 1
if (-not $bundle) { throw "No JavaScript emitted into $distssets." }
$bundleText = Get-Content $bundle.FullName -Raw
if ($bundleText -notmatch [regex]::Escape($ApiBaseUrl)) {
    throw "The bundle does not contain '$ApiBaseUrl'. VITE_API_BASE_URL was not applied, so every API call would go to the default /api/v1 and 404. Check web-admin\.env.production - a byte-order mark on the first line makes Vite ignore it."
}
Write-Ok "API base URL '$ApiBaseUrl' is baked into $($bundle.Name)."

$distWebConfig = Join-Path $dist 'web.config'
if (-not (Test-Path $distWebConfig)) {
    Write-Warn 'dist\web.config is missing. Deep links will 404 on refresh - check web-admin\public\web.config.'
}
else {
    # Keep the deep-link fallback pointed at the same prefix the bundle was built for.
    #
    # The 404 rule names a site-relative URL, so it has to agree with the IIS virtual path, which is
    # what VITE_BASE_URL already describes. Written by hand these two drift apart silently: the app
    # loads, and only a refresh or a pasted link shows that the fallback is aimed at the wrong prefix.
    # Deriving one from the other removes the chance.
    $wcXml  = [xml](Get-Content $distWebConfig)
    $errNode = $wcXml.SelectSingleNode('//httpErrors/error[@statusCode="404"]')
    if ($errNode) {
        $target = ($BaseUrl.TrimEnd('/')) + '/index.html'
        if ($errNode.GetAttribute('path') -ne $target -or $errNode.GetAttribute('responseMode') -ne 'ExecuteURL') {
            $errNode.SetAttribute('path', $target)
            $errNode.SetAttribute('responseMode', 'ExecuteURL')
            $wcXml.Save($distWebConfig)
        }
        Write-Ok "Deep-link fallback -> $target (ExecuteURL)"
    }
    else {
        Write-Warn 'dist\web.config has no 404 rule. Deep links will break on refresh.'
    }
}

Copy-Item -Path $dist -Destination $webOut -Recurse -Force
Write-Ok 'SPA staged.'

# ------------------------------------------------------------------ 5. deploy script

Write-Head '5. Server script'

$deployScript = Join-Path $PSScriptRoot 'vm-deploy.ps1'
if (Test-Path $deployScript) {
    Copy-Item $deployScript -Destination $OutputPath -Force
    Write-Ok 'vm-deploy.ps1 staged.'
}
else {
    Write-Warn "vm-deploy.ps1 not found beside this script - copy it to the VM yourself."
}

# ------------------------------------------------------------------ done

Write-Head 'Ready'
Write-Host ''
Write-Step "Copy this whole folder to the VM:  $OutputPath"
Write-Host ''
Write-Step 'Then, on the server, from inside the copied folder:'
Write-Host ''
Write-Host '    Unblock-File .\vm-deploy.ps1'
Write-Host '    .\vm-deploy.ps1'
Write-Host ''
Write-Step 'It stops the app with app_offline.htm, backs up what is there, copies both applications,'
Write-Step 'carries the existing connection string and signing key forward, and brings it back up.'
Write-Host ''

}
catch {
    Write-Host ''
    Write-Host '  FAILED' -ForegroundColor Red
    Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ''
    exit 1
}
