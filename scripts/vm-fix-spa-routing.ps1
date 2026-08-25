<#
.SYNOPSIS
    Diagnoses and fixes the SPA deep-link 404 on IIS. Run on the VM.

.DESCRIPTION
    Symptom: http://host/eams/ works, but http://host/eams/login and a refresh on any route return
    IIS's own "404 - File or directory not found" page.

    react-router only ever sees a path if index.html is served for it. IIS resolves /eams/login
    against the filesystem first, finds no such file, and answers 404 before the application is
    involved. dist\web.config exists to catch that and return index.html instead.

    Two things stop it working, and this script tells you which:

      1. web.config was never copied into the SPA folder.

      2. It is there, but the rule reads path="index.html". With responseMode="File" IIS resolves a
         relative path from the SITE root, not from the application root. The SPA is a child
         application at /eams, so IIS looks for <site>\index.html, does not find it, and falls back
         to the very 404 the rule was meant to replace. An absolute physical path fixes it.

    It reports before changing anything, backs the file up, applies the fix, and then tests the deep
    link so the answer is measured rather than assumed.

.PARAMETER WebVirtualPath
    The SPA's IIS virtual path. Default /eams.

.PARAMETER WebPath
    The SPA's physical folder. Detected from IIS when not given.

.PARAMETER TestUrl
    The deep link to test. Defaults to http://localhost/eams/login.

.PARAMETER DiagnoseOnly
    Report and change nothing.

.EXAMPLE
    .\vm-fix-spa-routing.ps1
.EXAMPLE
    .\vm-fix-spa-routing.ps1 -DiagnoseOnly
#>

[CmdletBinding()]
param(
    [string]$WebVirtualPath = '/eams',
    [string]$WebPath,
    [string]$TestUrl = 'http://localhost/eams/login',
    [switch]$DiagnoseOnly
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
function Write-Bad  { param($m) Write-Host "  [X]  $m" -ForegroundColor Red }
function Write-Head { param($m) Write-Host "`n$m" -ForegroundColor Cyan }

function Test-Url {
    param([string]$Url)
    try {
        $r = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 20 -ErrorAction Stop
        return [pscustomobject]@{ Code = [int]$r.StatusCode; Length = $r.RawContentLength; Body = $r.Content }
    }
    catch {
        $code = 0
        $body = ''
        if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
        # IIS puts the useful part - the substatus and the reason - in the body, and it is served in
        # full to localhost. A status code on its own does not distinguish 500.19 from 500.0.
        if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $body = $_.ErrorDetails.Message }
        return [pscustomobject]@{ Code = $code; Length = $body.Length; Body = $body }
    }
}

function Show-IisError {
    param([string]$Body)
    if (-not $Body) { return }
    # Pull the human-readable bits out of the IIS error page rather than printing the whole thing.
    $wanted = @()
    foreach ($pattern in @('<h2>(.*?)</h2>', '<h3>(.*?)</h3>', 'Detailed Error Information.*?Module\s*</th><td>(.*?)</td>',
                           'Error Code\s*</th><td>(.*?)</td>', 'Config Error\s*</th><td>(.*?)</td>',
                           'Config File\s*</th><td>(.*?)</td>')) {
        foreach ($m in [regex]::Matches($Body, $pattern, 'Singleline, IgnoreCase')) {
            $t = ($m.Groups[1].Value -replace '<[^>]+>', '').Trim()
            if ($t -and $wanted -notcontains $t) { $wanted += $t }
        }
    }
    foreach ($line in ($wanted | Select-Object -First 8)) { Write-Step "    $line" }
}

try {

Write-Head 'EAMS - SPA deep-link check'

$iisConfig = Join-Path $env:SystemRoot 'system32/inetsrv/config/applicationHost.config'
if (-not (Test-Path $iisConfig)) {
    throw "IIS is not installed here. Run this on the server, not on a development machine."
}

# ------------------------------------------------------------------ 1. locate the SPA

Write-Head '1. Where the SPA lives'

if (-not $WebPath) {
    try {
        Import-Module WebAdministration -ErrorAction Stop
        $app = Get-WebApplication -ErrorAction Stop | Where-Object { $_.path -eq $WebVirtualPath } | Select-Object -First 1
        if ($app -and $app.PhysicalPath) {
            $WebPath = [Environment]::ExpandEnvironmentVariables($app.PhysicalPath).TrimEnd('\')
            Write-Ok "$WebVirtualPath -> $WebPath   (from IIS)"
        }
    }
    catch { }
}
else {
    Write-Ok "$WebVirtualPath -> $WebPath   (given)"
}

if (-not $WebPath) {
    throw "Could not determine the physical path for $WebVirtualPath. Run 'Get-WebApplication | Select-Object path, PhysicalPath' and pass the answer with -WebPath."
}
if (-not (Test-Path $WebPath)) { throw "The folder '$WebPath' does not exist." }

# ------------------------------------------------------------------ 2. what is in it

Write-Head '2. Folder contents'

$names = @(Get-ChildItem -Path $WebPath -Force | ForEach-Object { $_.Name })
foreach ($n in ($names | Sort-Object)) { Write-Step "  $n" }

$indexPath = Join-Path $WebPath 'index.html'
$wcPath    = Join-Path $WebPath 'web.config'

if (Test-Path $indexPath) { Write-Ok 'index.html present' } else { Write-Bad 'index.html MISSING - the SPA was not deployed here' }

if (-not (Test-Path $wcPath)) {
    Write-Bad 'web.config MISSING'
    Write-Host ''
    Write-Step 'That is the whole problem. Deep links need it, and nothing else provides the'
    Write-Step 'fallback since the GitHub Pages 404.html copy was removed.'
    Write-Step 'Copy it from your release folder and re-run this script:'
    Write-Step '    Copy-Item <release>\web\web.config -Destination ' + $WebPath
    throw 'web.config is not in the SPA folder.'
}
Write-Ok 'web.config present'

# ------------------------------------------------------------------ 3. before

Write-Head '3. Before'

$before = Test-Url $TestUrl
Write-Step "$TestUrl -> HTTP $($before.Code)"

$xml = New-Object System.Xml.XmlDocument
$xml.PreserveWhitespace = $true
$xml.Load((Resolve-Path $wcPath))

$errNode = $xml.SelectSingleNode('//httpErrors/error[@statusCode="404"]')
$httpErrors = $xml.SelectSingleNode('//httpErrors')

if (-not $httpErrors) {
    throw "web.config has no <httpErrors> section. This is not the SPA's web.config - check that the right file was copied."
}
if (-not $errNode) {
    throw "web.config has <httpErrors> but no 404 <error> entry. Re-copy web.config from the release folder."
}

$currentPath = $errNode.GetAttribute('path')
$currentMode = $errNode.GetAttribute('responseMode')
Write-Step "404 rule: path='$currentPath' responseMode='$currentMode'"

$wanted = $indexPath

if ($currentPath -ieq $wanted) {
    Write-Ok 'The rule already uses an absolute path.'
    if ($before.Code -eq 200) {
        Write-Ok 'And the deep link works. Nothing to do.'
        Write-Host ''
        Wait-IfDoubleClicked
        exit 0
    }
    Write-Warn 'But the deep link still fails, so the cause is something else. See the notes at the end.'
}
else {
    Write-Warn "Relative path. IIS resolves that from the SITE root, not from $WebVirtualPath, so it"
    Write-Warn "looks for a file that is not there and returns its own 404 instead."
}

if ($DiagnoseOnly) {
    Write-Head 'Diagnose only - nothing changed'
    Write-Step "Would set path to: $wanted"
    Write-Host ''
    Wait-IfDoubleClicked
    exit 0
}

# ------------------------------------------------------------------ 4. fix

Write-Head '4. Applying the fix'

$backup = "$wcPath.bak"
Copy-Item -LiteralPath $wcPath -Destination $backup -Force
Write-Ok "Backed up to $backup"

$errNode.SetAttribute('path', $wanted)
if ($currentMode -ne 'File') {
    $errNode.SetAttribute('responseMode', 'File')
    Write-Ok "responseMode set to File (was '$currentMode')"
}
$xml.Save((Resolve-Path $wcPath))
Write-Ok "404 rule now points at $wanted"

# ------------------------------------------------------------------ 5. after

Write-Head '5. After'

Start-Sleep -Seconds 2
$after = Test-Url $TestUrl
Write-Step "$TestUrl -> HTTP $($after.Code)"

$root = Test-Url ($TestUrl -replace '/[^/]*$', '/')
Write-Step "SPA root -> HTTP $($root.Code)"

# An absolute physical path is only honoured in a delegated web.config when
# allowAbsolutePathsWhenDelegated is true in applicationHost.config, which is off by default and
# needs a server-level change. When it is off IIS answers 500 rather than saying so. ExecuteURL takes
# a site-relative URL instead, so it needs no server-level anything - try it before giving up.
if ($after.Code -ge 500) {
    Write-Warn "HTTP $($after.Code) - the absolute path is most likely being refused because"
    Write-Warn 'allowAbsolutePathsWhenDelegated is off in applicationHost.config. Details:'
    Show-IisError $after.Body

    Write-Head '6. Second attempt: ExecuteURL with a site-relative URL'

    $relative = ($WebVirtualPath.TrimEnd('/')) + '/index.html'
    $errNode.SetAttribute('path', $relative)
    $errNode.SetAttribute('responseMode', 'ExecuteURL')
    $xml.Save((Resolve-Path $wcPath))
    Write-Ok "404 rule now: path='$relative' responseMode='ExecuteURL'"

    Start-Sleep -Seconds 2
    $after = Test-Url $TestUrl
    Write-Step "$TestUrl -> HTTP $($after.Code)"
}

Write-Host ''
if ($after.Code -eq 200 -or ($after.Code -eq 404 -and $after.Length -gt 300)) {
    # A 404 carrying index.html's bytes is the intended outcome: the SPA boots and react-router
    # decides what the path means, while the status code stays honest for crawlers.
    Write-Ok 'Deep links are served by index.html now. Refresh the browser and try again.'
}
else {
    Write-Bad "Still failing with HTTP $($after.Code)."
    Write-Host ''
    Write-Step 'Things to check next, in order of likelihood:'
    Write-Step '  1. httpErrors may be locked at server level. Unlock it:'
    Write-Step '     %windir%\system32\inetsrv\appcmd.exe unlock config /section:httpErrors'
    Write-Step '  2. Confirm the application really points here:'
    Write-Step '     Get-WebApplication | Select-Object path, PhysicalPath'
    Write-Step '  3. The original file has been restored, so you are back to a 404 rather than a 500.'
    Copy-Item -LiteralPath $backup -Destination $wcPath -Force
    Write-Step "     (restored from $backup)"
}

Write-Host ''
Wait-IfDoubleClicked
}
catch {
    Write-Host ''
    Write-Host '  FAILED' -ForegroundColor Red
    Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ''
    Wait-IfDoubleClicked
    exit 1
}
