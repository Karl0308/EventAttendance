<#
.SYNOPSIS
    Deploys the API and the SPA on the IIS server. No administrator rights required.

.DESCRIPTION
    Run this ON THE VM, from inside the folder produced by scripts\build-release.ps1 (it contains
    api\, web\ and this script).

    It does the whole deployment:

        1. Backs up the current api\ and web\ folders
        2. Takes the API offline with app_offline.htm, which releases the file locks
        3. Copies both applications into place
        4. Restores the configuration the old deployment was using, and adds anything missing
        5. Brings the API back up and checks it answers

    NO ADMINISTRATOR RIGHTS. Stopping an application pool needs them; app_offline.htm does not — the
    ASP.NET Core Module sees the file appear, shuts the application down, and releases the DLLs. All
    this script needs is write access to the two application folders, which you already have because
    you are deploying into them.

    CONFIGURATION CARRIES FORWARD BY ITSELF. A publish overwrites web.config, which is where this
    deployment keeps ASPNETCORE_ENVIRONMENT and the CORS origin — so a naive copy silently discards
    them and the app starts with no connection string. This reads the old file before overwriting it
    and re-applies every environment variable it finds. Re-using the existing signing key is what
    keeps everyone signed in across a deploy.

    On a first deployment there is nothing to carry forward, so it prompts for the connection string
    and generates a signing key.

.PARAMETER ConnectionString
    Overrides the connection string. Prompted for only when none can be recovered.

.PARAMETER SigningKey
    Overrides the JWT signing key. Generated only when none can be recovered. Replacing an existing
    key signs out every live session.

.PARAMETER AllowedOrigin
    The SPA's origin: scheme + host, no path. Only applied when none is already configured.

    Note the default is http, not https — the Default Web Site here has no HTTPS binding, and a
    scheme mismatch fails CORS in the browser while the server logs nothing.

.PARAMETER Environment
    ASPNETCORE_ENVIRONMENT. Only applied when none is already configured.

.PARAMETER ApiPath
    The API application folder. Defaults to C:\inetpub\eams\api.

.PARAMETER WebPath
    The SPA application folder. Defaults to C:\inetpub\eams\web.

.PARAMETER SourcePath
    Where api\ and web\ are. Defaults to the folder containing this script.

.PARAMETER BackupPath
    Where to put the backup. Defaults to C:\backup\eams.

.PARAMETER ApiOnly / -WebOnly
    Deploy only one of the two.

.PARAMETER SkipBackup
    Do not back up first. Not recommended.

.EXAMPLE
    .\vm-deploy.ps1
#>

[CmdletBinding()]
param(
    [string]$ConnectionString,
    [string]$SigningKey,
    [string]$AllowedOrigin = 'http://dev.iloilosupermart.com',
    [ValidateSet('Production', 'Staging', 'Development')]
    [string]$Environment = 'Development',
    [string]$ApiPath = 'C:\inetpub\eams\api',
    [string]$WebPath = 'C:\inetpub\eams\web',
    [string]$SourcePath,
    [string]$BackupPath = 'C:\backup\eams',
    [switch]$ApiOnly,
    [switch]$WebOnly,
    [switch]$SkipBackup
)

$ErrorActionPreference = 'Stop'

$launchedByDoubleClick = $false
try {
    $parent = (Get-CimInstance Win32_Process -Filter "ProcessId = $PID" -ErrorAction Stop).ParentProcessId
    $launchedByDoubleClick = (Get-Process -Id $parent -ErrorAction Stop).ProcessName -in @('explorer', 'Explorer')
}
catch { }
function Wait-IfDoubleClicked { if ($launchedByDoubleClick) { Write-Host ''; Read-Host 'Press Enter to close' } }

function Write-Step { param($m) Write-Host "  $m" }
function Write-Ok   { param($m) Write-Host "  [ok] $m" -ForegroundColor Green }
function Write-Warn { param($m) Write-Host "  [!]  $m" -ForegroundColor Yellow }
function Write-Head { param($m) Write-Host "`n$m" -ForegroundColor Cyan }

$JwtVar  = 'Jwt__SigningKey'
$ConnVar = 'ConnectionStrings__EamsDb'
$CorsVar = 'Cors__AllowedOrigins__0'
$EnvVar  = 'ASPNETCORE_ENVIRONMENT'

if (-not $SourcePath) { $SourcePath = $PSScriptRoot }
$srcApi = Join-Path $SourcePath 'api'
$srcWeb = Join-Path $SourcePath 'web'

$doApi = -not $WebOnly
$doWeb = -not $ApiOnly

try {

Write-Head 'EAMS — deploy'

if ($doApi -and -not (Test-Path (Join-Path $srcApi 'EAMS.Api.dll'))) {
    throw "No EAMS.Api.dll in '$srcApi'. Run scripts\build-release.ps1 and copy the release folder here."
}
if ($doWeb -and -not (Test-Path (Join-Path $srcWeb 'index.html'))) {
    throw "No index.html in '$srcWeb'. Run scripts\build-release.ps1 and copy the release folder here."
}

Write-Ok "Source: $SourcePath"
if ($doApi) { Write-Ok "API  -> $ApiPath" }
if ($doWeb) { Write-Ok "SPA  -> $WebPath" }

# ------------------------------------------------------------------ read the old configuration

function Read-EnvFromWebConfig {
    param([string]$Path)
    $found = @{}
    if (-not (Test-Path $Path)) { return $found }
    try {
        $xml = New-Object System.Xml.XmlDocument
        $xml.Load((Resolve-Path $Path))
        foreach ($n in $xml.SelectNodes('//aspNetCore/environmentVariables/environmentVariable')) {
            $found[$n.GetAttribute('name')] = $n.GetAttribute('value')
        }
    }
    catch { Write-Warn "Could not parse the existing web.config: $($_.Exception.Message)" }
    return $found
}

$deployedConfig = Join-Path $ApiPath 'web.config'
$carried = @{}

if ($doApi) {
    Write-Head '1. Existing configuration'
    $carried = Read-EnvFromWebConfig $deployedConfig
    if ($carried.Count -gt 0) {
        Write-Ok "Recovered $($carried.Count) setting(s) from the current deployment:"
        foreach ($k in ($carried.Keys | Sort-Object)) { Write-Step "  $k" }
        Write-Step 'These are re-applied after the copy, so a publish does not discard them.'
    }
    else {
        Write-Warn 'Nothing to carry forward — treating this as a first deployment.'
    }
}

# ------------------------------------------------------------------ back up

if (-not $SkipBackup) {
    Write-Head '2. Backup'
    $stamp  = Get-Date -Format 'yyyyMMdd-HHmmss'
    $target = Join-Path $BackupPath $stamp
    [void](New-Item -ItemType Directory -Path $target -Force)

    if ($doApi -and (Test-Path $ApiPath)) {
        Copy-Item $ApiPath -Destination (Join-Path $target 'api') -Recurse -Force
        Write-Ok "api  -> $target\api"
    }
    if ($doWeb -and (Test-Path $WebPath)) {
        Copy-Item $WebPath -Destination (Join-Path $target 'web') -Recurse -Force
        Write-Ok "web  -> $target\web"
    }
    Write-Step 'Roll back by copying these folders back and deleting app_offline.htm.'
}

# ------------------------------------------------------------------ take the API offline

$offline = Join-Path $ApiPath 'app_offline.htm'

if ($doApi) {
    Write-Head '3. Taking the API offline'
    [void](New-Item -ItemType Directory -Path $ApiPath -Force)
    Set-Content -Path $offline -Encoding utf8 -Value @'
<!doctype html>
<title>EAMS is updating</title>
<body style="font-family:system-ui;margin:4rem auto;max-width:32rem">
<h1>Updating</h1>
<p>The attendance system is being updated and will be back shortly.</p>
</body>
'@
    Write-Ok 'app_offline.htm written — the module shuts the app down and releases the DLLs.'
    Start-Sleep -Seconds 3
}

# ------------------------------------------------------------------ copy

if ($doApi) {
    Write-Head '4. Copying the API'
    # Everything except app_offline.htm, which must stay until the end.
    Get-ChildItem -Path $ApiPath -Force |
        Where-Object { $_.Name -ne 'app_offline.htm' } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    Copy-Item -Path (Join-Path $srcApi '*') -Destination $ApiPath -Recurse -Force
    if (-not (Test-Path (Join-Path $ApiPath 'EAMS.Api.dll'))) { throw 'The copy did not land — EAMS.Api.dll is not in the target.' }
    Write-Ok 'API files copied.'
}

if ($doWeb) {
    Write-Head '5. Copying the SPA'
    [void](New-Item -ItemType Directory -Path $WebPath -Force)
    Get-ChildItem -Path $WebPath -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item -Path (Join-Path $srcWeb '*') -Destination $WebPath -Recurse -Force

    if (-not (Test-Path (Join-Path $WebPath 'index.html'))) { throw 'The copy did not land — index.html is not in the target.' }
    if (-not (Test-Path (Join-Path $WebPath 'web.config'))) { Write-Warn 'web.config is missing — deep links will 404 on refresh.' }
    Write-Ok 'SPA files copied.'
}

# ------------------------------------------------------------------ restore configuration

if ($doApi) {
    Write-Head '6. Configuration'

    $xml = New-Object System.Xml.XmlDocument
    $xml.PreserveWhitespace = $true
    $xml.Load((Resolve-Path $deployedConfig))

    $aspNetCore = $xml.SelectSingleNode('//aspNetCore')
    if (-not $aspNetCore) { throw "No <aspNetCore> element in $deployedConfig." }

    $envNode = $aspNetCore.SelectSingleNode('environmentVariables')
    if (-not $envNode) {
        $envNode = $xml.CreateElement('environmentVariables')
        [void]$aspNetCore.AppendChild($envNode)
    }

    function Set-Env {
        param([string]$Name, [string]$Value)
        $existing = $envNode.SelectSingleNode("environmentVariable[@name='$Name']")
        if ($existing) { $existing.SetAttribute('value', $Value); return }
        $e = $xml.CreateElement('environmentVariable')
        $e.SetAttribute('name', $Name)
        $e.SetAttribute('value', $Value)
        [void]$envNode.AppendChild($e)
    }

    # Everything the old deployment had, first. Explicit parameters override below.
    foreach ($k in $carried.Keys) { Set-Env $k $carried[$k] }

    # Connection string
    if ($ConnectionString) { Set-Env $ConnVar $ConnectionString; Write-Ok 'Connection string set from the parameter.' }
    elseif ($carried.ContainsKey($ConnVar)) { Write-Ok 'Connection string carried forward.' }
    else {
        Write-Step 'Example: Server=.;Database=EAMS;User Id=eams_app;Password=...;TrustServerCertificate=True'
        $entered = Read-Host 'Enter the EamsDb connection string'
        if (-not $entered) { throw 'A connection string is required — the host will not start without one.' }
        Set-Env $ConnVar $entered
        Write-Ok 'Connection string set.'
    }

    # Signing key
    $newKey = $null
    if ($SigningKey) {
        if ($SigningKey.Length -lt 32) { throw 'The supplied signing key is under 32 characters; the host will refuse to start.' }
        Set-Env $JwtVar $SigningKey
        Write-Warn 'Signing key replaced — every live session is now invalid.'
    }
    elseif ($carried.ContainsKey($JwtVar)) {
        Write-Ok 'Signing key carried forward — existing sessions survive this deploy.'
    }
    else {
        $bytes = New-Object byte[] 64
        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
        $newKey = [Convert]::ToBase64String($bytes)
        Set-Env $JwtVar $newKey
        Write-Ok 'Signing key generated (none was configured).'
    }

    if (-not $carried.ContainsKey($CorsVar)) { Set-Env $CorsVar $AllowedOrigin.TrimEnd('/'); Write-Ok "CORS origin set to $($AllowedOrigin.TrimEnd('/'))." }
    else { Write-Ok "CORS origin carried forward: $($carried[$CorsVar])" }

    if (-not $carried.ContainsKey($EnvVar)) { Set-Env $EnvVar $Environment; Write-Ok "Environment set to $Environment." }
    else { Write-Ok "Environment carried forward: $($carried[$EnvVar])" }

    $xml.Save((Resolve-Path $deployedConfig))
    Write-Ok 'web.config written.'

    foreach ($required in @($ConnVar, $JwtVar)) {
        $check = Read-EnvFromWebConfig $deployedConfig
        if (-not $check.ContainsKey($required)) { throw "'$required' is not in the saved web.config. The application will not start." }
    }
    Write-Ok 'Verified both required settings are present.'
}

# ------------------------------------------------------------------ back online

if ($doApi) {
    Write-Head '7. Bringing the API back'
    Remove-Item $offline -Force -ErrorAction SilentlyContinue
    Write-Ok 'app_offline.htm removed.'
    Start-Sleep -Seconds 5
}

# ------------------------------------------------------------------ verify

Write-Head '8. Verify'

function Test-Url {
    param([string]$Url, [string]$Label)
    try {
        $r = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 30 -ErrorAction Stop
        Write-Ok "$Label -> HTTP $($r.StatusCode)"
        return $true
    }
    catch {
        $code = $_.Exception.Response.StatusCode.value__
        if ($code) { Write-Warn "$Label -> HTTP $code" } else { Write-Warn "$Label -> $($_.Exception.Message)" }
        return $false
    }
}

$healthy = $true
if ($doApi) { $healthy = (Test-Url 'http://localhost/eamsapi/api/v1/events' 'API   /eamsapi/api/v1/events') -and $healthy }
if ($doWeb) { $healthy = (Test-Url 'http://localhost/eams/' 'SPA   /eams/') -and $healthy }

if (-not $healthy) {
    Write-Host ''
    Write-Warn 'Something did not answer. Where to look:'
    Write-Step '  500.30  -> the app threw at startup. Set stdoutLogEnabled="true" in web.config,'
    Write-Step '             create a logs\ folder (the module will not), reproduce, read logs\stdout_*.log'
    Write-Step '  500.19  -> the ASP.NET Core Hosting Bundle is missing, or was installed before IIS'
    Write-Step '  404     -> the IIS application or its physical path is wrong'
}

# ------------------------------------------------------------------ done

if ($newKey) {
    Write-Head 'SIGNING KEY — copy it now, it is not printed again'
    Write-Host ''
    Write-Host "    $newKey"
    Write-Host ''
}

Write-Head 'Next: the administrator account'
Write-Host ''
Write-Step 'Only needed once. Nothing creates one for you.'
Write-Host ''
Write-Host "    cd $ApiPath"
Write-Host '    $env:ConnectionStrings__EamsDb = "<your connection string>"'
Write-Host '    $env:Jwt__SigningKey            = "<the signing key>"'
Write-Host '    dotnet EAMS.Api.dll create-admin --email admin@usa.edu.ph --name "Full Name" --role SuperAdmin'
Write-Host ''
Write-Step 'Both $env: lines are required: create-admin runs outside IIS, so it cannot see what is in'
Write-Step 'web.config, and it runs on the fully built host so it refuses to start without a key it'
Write-Step 'never actually uses.'
Write-Host ''
Write-Step 'The signing key is in web.config if you need to read it back.'
Write-Host ''

Wait-IfDoubleClicked
}
catch {
    Write-Host ''
    Write-Host '  FAILED' -ForegroundColor Red
    Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ''
    if (Test-Path (Join-Path $ApiPath 'app_offline.htm')) {
        Write-Warn 'app_offline.htm is still in place, so the API is DOWN.'
        Write-Step "Remove it to bring the old build back:  Remove-Item '$(Join-Path $ApiPath 'app_offline.htm')'"
        Write-Step "Or restore from $BackupPath first if files were already replaced."
    }
    Write-Host ''
    Wait-IfDoubleClicked
    exit 1
}
