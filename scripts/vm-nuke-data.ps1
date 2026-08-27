<#
.SYNOPSIS
    Wipes EAMS data on the development VM. Destructive and deliberate.

.DESCRIPTION
    Run this ON THE VM. It empties the EAMS database so the next start of the API comes up on a
    clean slate, and it does nothing else - it does not deploy, does not change configuration, and
    does not touch either application folder beyond taking the API offline while it works.

    THREE MODES, from narrowest to widest:

        -Mode Roster   (default)  Every academic, event, attendance and import row.
                                  KEEPS the school, your login, roles, permissions and devices.
                                  This is the one you want after a bad roster import.

        -Mode All                 Every row in every table except __EFMigrationsHistory.
                                  The schema survives; the data does not. On the next start the
                                  API re-seeds RBAC reference data (every environment) and the
                                  development fixtures (Development only).
                                  YOU LOSE THE ADMIN ACCOUNT create-admin made.

        -Mode Drop                DROP DATABASE. The next start rebuilds it from migrations and
                                  seeds it. Use when the schema itself is suspect.

    IT REFUSES TO RUN AGAINST A NON-DEVELOPMENT DEPLOYMENT. The environment is read from the
    deployed web.config, which is where this VM keeps its configuration - not from app-pool
    settings, which create-admin and this script cannot see. Anything other than Development stops
    the script unless you pass -IAmSureThisIsNotProduction, which exists so that overriding is a
    sentence you had to type rather than a flag you could fat-finger.

    NOTHING HAPPENS UNTIL YOU TYPE THE DATABASE NAME. The plan - mode, server, database, every
    table and its live row count - is printed first, and the confirmation prompt wants the database
    name back, exactly. -Force skips the prompt for unattended use; -DryRun prints the plan and
    exits without touching anything.

    THE API GOES OFFLINE WHILE IT WORKS. app_offline.htm stops the application writing rows behind
    the script and, for -Mode Drop, is what lets the database be dropped at all - an open pooled
    connection blocks it. The file is removed again at the end, including when the wipe fails.
    No administrator rights: app_offline.htm needs none, stopping an app pool does.

    HOW THE DELETE WORKS. Foreign keys are disabled across the whole database, every targeted table
    is emptied, and the constraints are re-enabled WITH CHECK inside one transaction. Re-enabling
    validates what is left, so if a kept row still points at a deleted one the whole thing rolls
    back and names the constraint instead of leaving the database quietly inconsistent. Identity
    columns are reseeded; today there are none, and the day someone adds one this keeps working.

    SQL Server 2012 is the floor (that is what this VM runs), so there is no syntax here newer than
    that. The connection is made with System.Data.SqlClient, so neither sqlcmd nor the SqlServer
    module has to be installed.

.PARAMETER Mode
    Roster (default), All, or Drop. See above.

.PARAMETER ConnectionString
    Overrides the connection string. Read from the deployed web.config when omitted.

.PARAMETER ApiPath
    The API application folder, which is where web.config is read from and app_offline.htm written
    to. Asked of IIS when omitted; C:\inetpub\wwwroot\eamsapi is the fallback.

.PARAMETER ApiVirtualPath
    The API's virtual path, used to ask IIS where it lives. Defaults to /eamsapi.

.PARAMETER DryRun
    Print the plan and the row counts, change nothing. Safe to run any time.

.PARAMETER Force
    Skip the typed confirmation. For unattended use only.

.PARAMETER NoOffline
    Do not write app_offline.htm. The application keeps running and can write rows while the script
    deletes them. Refused with -Mode Drop.

.PARAMETER IAmSureThisIsNotProduction
    Proceed even though the deployed environment is not Development.

.EXAMPLE
    .\vm-nuke-data.ps1 -DryRun
    Shows what a Roster wipe would remove, and how many rows are in each table.

.EXAMPLE
    .\vm-nuke-data.ps1
    Wipes roster, events, attendance and import data. Keeps the school, your login and the devices.

.EXAMPLE
    .\vm-nuke-data.ps1 -Mode All
    Empties every table. The next start re-seeds RBAC and the development fixtures.

.EXAMPLE
    .\vm-nuke-data.ps1 -Mode Drop -Force
    Drops the database without prompting. The next start rebuilds it from migrations.
#>

[CmdletBinding()]
param(
    [ValidateSet('Roster', 'All', 'Drop')]
    [string]$Mode = 'Roster',
    [string]$ConnectionString,
    [string]$ApiPath,
    [string]$ApiVirtualPath = '/eamsapi',
    [switch]$DryRun,
    [switch]$Force,
    [switch]$NoOffline,
    [switch]$IAmSureThisIsNotProduction
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

$ConnVar = 'ConnectionStrings__EamsDb'
$EnvVar  = 'ASPNETCORE_ENVIRONMENT'

# ---------------------------------------------------------------------------- what each mode keeps
#
# A keep-list rather than a wipe-list, and the direction is the safety property: a table added to
# the schema next month is wiped by default instead of being silently left behind, half-populated,
# pointing at rows that no longer exist. Names are matched against sys.tables, so a table dropped
# from the schema costs nothing here either.
#
# __EFMigrationsHistory is never touched outside -Mode Drop. Emptying it tells EF the schema is
# unmigrated and the next start re-runs every migration against tables that already exist.
$KeepAlways = @('__EFMigrationsHistory')

# Roster mode keeps the tenant and the people who log in: the school row (which is also what makes
# the development seed return early, so fixtures do NOT come back), the RBAC reference data, live
# sessions, and the enrolled devices whose API keys the kiosks hold.
$KeepRoster = $KeepAlways + @(
    'Schools',
    'Users', 'Roles', 'Permissions', 'UserRoles', 'RolePermissions', 'RefreshTokens',
    'Devices',
    'SystemSettings'
)

# --------------------------------------------------------------------------------- where things are

function Resolve-IisPhysicalPath {
    param([string]$VirtualPath)
    try {
        Import-Module WebAdministration -ErrorAction Stop
        $app = Get-WebApplication -ErrorAction Stop |
               Where-Object { $_.path -eq $VirtualPath } |
               Select-Object -First 1
        if ($app -and $app.PhysicalPath) {
            return [Environment]::ExpandEnvironmentVariables($app.PhysicalPath).TrimEnd('\')
        }
    }
    catch { }
    return $null
}

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
    catch { Write-Warn "Could not parse web.config: $($_.Exception.Message)" }
    return $found
}

Write-Head 'EAMS - wipe development data'

if ($NoOffline -and $Mode -eq 'Drop') {
    Write-Bad '-NoOffline cannot be used with -Mode Drop: an open connection blocks DROP DATABASE.'
    Wait-IfDoubleClicked
    exit 1
}

if (-not $PSBoundParameters.ContainsKey('ApiPath')) {
    $detected = Resolve-IisPhysicalPath $ApiVirtualPath
    if ($detected) {
        $ApiPath = $detected
        Write-Ok "IIS says $ApiVirtualPath is $ApiPath"
    }
    else {
        $ApiPath = 'C:\inetpub\wwwroot\eamsapi'
        Write-Warn "IIS could not be asked where $ApiVirtualPath lives; assuming $ApiPath"
    }
}

$config = Read-EnvFromWebConfig (Join-Path $ApiPath 'web.config')

# ------------------------------------------------------------------------------ the environment gate

$deployedEnv = $config[$EnvVar]
if (-not $deployedEnv) { $deployedEnv = '(not set)' }

if ($deployedEnv -ne 'Development') {
    Write-Bad "The deployed environment is '$deployedEnv', not Development."
    if (-not $IAmSureThisIsNotProduction) {
        Write-Step 'This script only runs against a Development deployment. If this really is the'
        Write-Step 'development VM and the variable is merely wrong, fix the variable - or, if you'
        Write-Step 'know exactly what you are doing, re-run with -IAmSureThisIsNotProduction.'
        Wait-IfDoubleClicked
        exit 1
    }
    Write-Warn 'Continuing anyway because -IAmSureThisIsNotProduction was passed.'
}
else {
    Write-Ok 'Deployed environment is Development.'
}

# ------------------------------------------------------------------------------ the connection

if (-not $ConnectionString) { $ConnectionString = $config[$ConnVar] }
if (-not $ConnectionString) {
    Write-Bad "No connection string. '$ConnVar' is not in $ApiPath\web.config and -ConnectionString was not passed."
    Wait-IfDoubleClicked
    exit 1
}

try {
    $csb = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $ConnectionString
}
catch {
    Write-Bad "The connection string will not parse: $($_.Exception.Message)"
    Wait-IfDoubleClicked
    exit 1
}

$server   = $csb.DataSource
$database = $csb.InitialCatalog

if (-not $database) {
    Write-Bad 'The connection string names no database (no Initial Catalog / Database).'
    Wait-IfDoubleClicked
    exit 1
}

# Long enough that a hung wipe is visibly hung rather than a timeout that looks like a failure.
$csb['Connect Timeout'] = 15
$appConn = $csb.ConnectionString

function Invoke-Sql {
    param(
        [string]$Sql,
        [string]$ConnStr = $appConn,
        [int]$TimeoutSeconds = 600
    )
    $conn = New-Object System.Data.SqlClient.SqlConnection $ConnStr
    try {
        $conn.Open()
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $Sql
        $cmd.CommandTimeout = $TimeoutSeconds
        $reader = $cmd.ExecuteReader()
        $rows = @()
        while ($reader.Read()) {
            $row = @{}
            for ($i = 0; $i -lt $reader.FieldCount; $i++) {
                $row[$reader.GetName($i)] = if ($reader.IsDBNull($i)) { $null } else { $reader.GetValue($i) }
            }
            $rows += [pscustomobject]$row
        }
        $reader.Close()
        return $rows
    }
    finally { $conn.Dispose() }
}

Write-Head '1. Target'
Write-Step "Server    : $server"
Write-Step "Database  : $database"
Write-Step "Mode      : $Mode"

try {
    $probe = Invoke-Sql "SELECT SERVERPROPERTY('ProductVersion') AS version"
    Write-Ok "Connected. SQL Server $($probe[0].version)"
}
catch {
    Write-Bad "Cannot connect: $($_.Exception.Message)"
    Wait-IfDoubleClicked
    exit 1
}

# ------------------------------------------------------------------------------ the plan

Write-Head '2. Plan'

$allTables = Invoke-Sql "SELECT s.name AS [schema], t.name AS [table] FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE t.is_ms_shipped = 0 ORDER BY s.name, t.name"

if ($allTables.Count -eq 0) {
    Write-Warn 'The database has no user tables. Nothing to wipe.'
    Wait-IfDoubleClicked
    exit 0
}

switch ($Mode) {
    'Roster' { $keep = $KeepRoster }
    'All'    { $keep = $KeepAlways }
    'Drop'   { $keep = @() }
}

$targets = @()
$kept    = @()
foreach ($t in $allTables) {
    if ($Mode -ne 'Drop' -and ($keep -contains $t.table)) { $kept += $t } else { $targets += $t }
}

if ($Mode -eq 'Drop') {
    Write-Step "DROP DATABASE [$database] - schema and data, all of it."
    Write-Step "$($allTables.Count) table(s) go with it."
}
else {
    Write-Step "$($targets.Count) table(s) will be emptied:"
    $total = 0
    foreach ($t in $targets) {
        $n = (Invoke-Sql "SELECT COUNT_BIG(*) AS n FROM [$($t.schema)].[$($t.table)]")[0].n
        $total += $n
        Write-Host ("      {0,-30} {1,12:N0}" -f $t.table, $n)
    }
    Write-Step ("{0,-36} {1,12:N0}" -f 'TOTAL ROWS TO DELETE', $total)

    if ($kept.Count -gt 0) {
        Write-Host ''
        Write-Step "$($kept.Count) table(s) will be left alone:"
        foreach ($t in $kept) {
            $n = (Invoke-Sql "SELECT COUNT_BIG(*) AS n FROM [$($t.schema)].[$($t.table)]")[0].n
            Write-Host ("      {0,-30} {1,12:N0}" -f $t.table, $n)
        }
    }
}

# ------------------------------------------------------------------------ what you will have to redo

Write-Head '3. What this costs you afterwards'

switch ($Mode) {
    'Roster' {
        Write-Step 'You keep your login, the school, the roles and the enrolled devices.'
        Write-Step 'Terms go, and the seed will NOT bring one back - it returns early while a School'
        Write-Step 'row exists. Create one on the SPA /terms page before the next roster import; the'
        Write-Step 'import page shows an empty picker and refuses to stage a batch without one.'
        Write-Step 'Do not hand-write INSERT INTO dbo.Terms - the route enforces rules SQL does not.'
    }
    'All' {
        Write-Warn 'THE ADMIN ACCOUNT GOES. If yours was made by create-admin it will not come back'
        Write-Step 'on its own - re-run create-admin, or set Seed:DevelopmentSuperAdminPassword and'
        Write-Step 'let the Development seed make dev-admin@usa.edu.ph.'
        Write-Step 'RBAC reference data is re-seeded in every environment on the next start.'
        Write-Step 'The development fixtures come back too, because no School row is left to stop them.'
    }
    'Drop' {
        Write-Step 'The next start runs every migration and then seeds, so the database comes back as'
        Write-Step 'a fresh install - fixtures, RBAC, and a development admin if the seed password is'
        Write-Step 'configured. Same caveat as -Mode All about create-admin accounts.'
    }
}

if ($DryRun) {
    Write-Head 'Dry run - nothing was changed.'
    Wait-IfDoubleClicked
    exit 0
}

# ------------------------------------------------------------------------------ confirmation

Write-Head '4. Confirm'

if (-not $Force) {
    Write-Warn 'This cannot be undone. There is no backup step in this script.'
    $typed = Read-Host "  Type the database name ($database) to proceed"
    if ($typed -ne $database) {
        Write-Bad 'That is not the database name. Nothing was changed.'
        Wait-IfDoubleClicked
        exit 1
    }
}
else {
    Write-Warn '-Force: proceeding without confirmation.'
}

# ------------------------------------------------------------------------------ take the API offline

$offline = Join-Path $ApiPath 'app_offline.htm'
$wroteOffline = $false

if (-not $NoOffline) {
    Write-Head '5. Taking the API offline'
    try {
        Set-Content -Path $offline -Value 'EAMS is being reset. Back shortly.' -Encoding ASCII
        $wroteOffline = $true
        Write-Ok 'app_offline.htm written - the module shuts the application down.'
        # The module needs a moment to drain. A pooled connection still holding the database is what
        # makes DROP fail, and a request mid-flight is what writes a row behind the delete.
        Start-Sleep -Seconds 5
    }
    catch {
        Write-Warn "Could not write app_offline.htm: $($_.Exception.Message)"
        Write-Warn 'The application may write rows while this runs.'
    }
}
else {
    Write-Warn '-NoOffline: the application stays up and may write rows while this runs.'
}

# ------------------------------------------------------------------------------ do it

$failed = $null

try {
    Write-Head '6. Wiping'

    if ($Mode -eq 'Drop') {
        $masterCsb = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $appConn
        # Indexed with the keyword's real spelling, space and all. PowerShell routes a property SET on
        # this class through DbConnectionStringBuilder's dictionary, so `.InitialCatalog = 'master'`
        # looks right, compiles, runs, and throws "Keyword not supported: 'InitialCatalog'" - the
        # property name is not the connection-string keyword. Reads are fine, which is what makes it
        # easy to miss. Pooling off so this connection cannot be the one holding the database open.
        $masterCsb['Initial Catalog'] = 'master'
        $masterCsb['Pooling'] = $false

        # SINGLE_USER WITH ROLLBACK IMMEDIATE evicts whatever is still connected - app_offline.htm
        # closes the application's pool, but a forgotten SSMS window is exactly as blocking.
        $dropSql = "IF DB_ID(N'$database') IS NOT NULL BEGIN ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$database]; END"
        Invoke-Sql -ConnStr $masterCsb.ConnectionString -Sql $dropSql
        Write-Ok "Dropped [$database]."
    }
    else {
        $targetList = @($targets | ForEach-Object { "[$($_.schema)].[$($_.table)]" })

        # One batch, one transaction, XACT_ABORT so any error rolls the whole thing back rather than
        # leaving half a wipe. Constraints come off across the entire database - not only the target
        # tables - because a kept table can hold a foreign key INTO a target, and the delete order
        # stops mattering once none of them are checked.
        $sql = New-Object System.Text.StringBuilder
        [void]$sql.AppendLine('SET NOCOUNT ON;')
        [void]$sql.AppendLine('SET XACT_ABORT ON;')
        [void]$sql.AppendLine('BEGIN TRANSACTION;')
        [void]$sql.AppendLine('EXEC sp_MSforeachtable @command1 = "ALTER TABLE ? NOCHECK CONSTRAINT ALL";')
        foreach ($t in $targetList) { [void]$sql.AppendLine("DELETE FROM $t;") }
        [void]$sql.AppendLine('EXEC sp_MSforeachtable @command1 = "ALTER TABLE ? WITH CHECK CHECK CONSTRAINT ALL";')
        [void]$sql.AppendLine('COMMIT TRANSACTION;')

        Invoke-Sql -Sql $sql.ToString()
        Write-Ok "Emptied $($targets.Count) table(s)."

        # Defensive, and cheap: there are no identity columns in this schema today. The day one
        # arrives, a wipe that left the seed where it was would hand the fresh data ids starting in
        # the tens of thousands, which reads as corruption long after the cause is forgotten.
        $identities = Invoke-Sql "SELECT s.name AS [schema], t.name AS [table] FROM sys.identity_columns c JOIN sys.tables t ON t.object_id = c.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE t.is_ms_shipped = 0"
        foreach ($i in $identities) {
            if ($targetList -contains "[$($i.schema)].[$($i.table)]") {
                Invoke-Sql "DBCC CHECKIDENT ('[$($i.schema)].[$($i.table)]', RESEED, 0) WITH NO_INFOMSGS"
                Write-Step "Reseeded identity on $($i.table)."
            }
        }

        # Said out loud rather than trusted: a constraint left NOCHECK is untrusted, the optimizer
        # stops using it, and nothing ever complains.
        $untrusted = Invoke-Sql 'SELECT COUNT(*) AS n FROM sys.foreign_keys WHERE is_not_trusted = 1'
        if ($untrusted[0].n -gt 0) {
            Write-Warn "$($untrusted[0].n) foreign key(s) are still untrusted. Re-enable them with:"
            Write-Step '  EXEC sp_MSforeachtable "ALTER TABLE ? WITH CHECK CHECK CONSTRAINT ALL";'
        }
        else {
            Write-Ok 'All foreign keys re-enabled and trusted.'
        }
    }
}
catch {
    $failed = $_
    Write-Bad "The wipe failed: $($_.Exception.Message)"
    if ($Mode -eq 'Drop') {
        # No transaction wraps a DROP, so promising a rollback here would be a lie. The database is
        # either gone or untouched, and which one it is depends on where this threw.
        Write-Step "Check whether the database still exists before re-running: it was not dropped inside a transaction."
    }
    else {
        Write-Step 'The transaction rolled back, so the data is as it was.'
    }
}
finally {
    if ($wroteOffline) {
        Write-Head '7. Bringing the API back'
        try {
            Remove-Item $offline -Force -ErrorAction Stop
            Write-Ok 'app_offline.htm removed.'
        }
        catch {
            Write-Bad 'Could not remove app_offline.htm - THE API IS STILL DOWN.'
            Write-Step "Delete it by hand: Remove-Item '$offline'"
        }
    }
}

if ($failed) {
    Wait-IfDoubleClicked
    exit 1
}

# ------------------------------------------------------------------------------ afterwards

Write-Head 'Done'
Write-Step 'The API rebuilds what it can on its next start: migrations first, then the seed.'
Write-Step 'Wake it up:  Invoke-WebRequest http://localhost/eamsapi/health -UseBasicParsing'
Write-Step '(no curl.exe on this Windows build - Invoke-WebRequest is the one that exists)'

if ($Mode -eq 'Roster') {
    Write-Step 'Then create a term on /terms before importing a roster.'
}

Wait-IfDoubleClicked
