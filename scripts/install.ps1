<#
.SYNOPSIS
  Installs (or updates) the ERP on this computer so that it starts by itself with Windows, without anyone signing in.

.DESCRIPTION
  Run in PowerShell opened as Administrator. What it sets up:
    1. PostgreSQL 17 as the Windows service "ErpDatabase" (its own data folder and port, local connections only). Uses an
       existing PostgreSQL 17 installation's programs, or installs PostgreSQL 17 with winget.
    2. New passwords and keys in <InstallDir>\config\erp.json (readable only by administrators and the ERP service).
    3. The ERP as the Windows service "ErpApi": starts at boot after the database, restarts itself if it stops, runs under its
       own low-privilege account and only listens on this computer (Tailscale publishes it to your devices).
    4. The nightly backup (as SYSTEM, so it runs without anyone signed in), tested once now.
    5. Tailscale publishing at https://<computer>.<tailnet>.ts.net when Tailscale is installed and signed in.

  Running it again with a newer package updates the program and keeps the data, passwords and keys. Database changes are
  applied when the service starts.

  The package is the folder this script is in (made by package.ps1); in the source code it builds one first.

.EXAMPLE
  # A new shop computer, with the first back-office administrator and a second backup copy on a USB disk:
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -AdminEmail you@example.com -AdminPassword 'A-Strong-Password-2026' -BackupCopyTo E:\ERP-backups

  # Update after copying a newer package:
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1

  # Bring existing data along (a backup, and the settings file holding the keys it was encrypted with):
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -ImportDump backups\erp-20261021-230000.dump -ImportKeysFrom old\erp.json

  # Remove the two services (the data folder and backups stay):
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Uninstall
#>
param(
    [string]$InstallDir = 'C:\ERP',
    [int]$Port = 5080,
    [int]$DbPort = 5434,
    [string]$Package,
    [string]$AdminEmail,
    [string]$AdminPassword,
    [string]$ImportDump,
    [string]$ImportKeysFrom,
    [string]$BackupCopyTo,
    [string]$BackupAt = '23:30',
    [string]$PublicUrl,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$ApiService = 'ErpApi'
$DbService = 'ErpDatabase'
$configDir = Join-Path $InstallDir 'config'
$configFile = Join-Path $configDir 'erp.json'
$pgData = Join-Path $InstallDir 'pgdata'
$appDir = Join-Path $InstallDir 'app'

function Step([string]$text) { Write-Host "`n== $text" -ForegroundColor Cyan }

function Secret([int]$bytes, [switch]$Base64) {
    $b = New-Object byte[] $bytes
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
    if ($Base64) { return [Convert]::ToBase64String($b) }
    return ([BitConverter]::ToString($b) -replace '-', '').ToLowerInvariant()
}

function Wait-Until([scriptblock]$check, [int]$seconds, [string]$what) {
    for ($i = 0; $i -lt $seconds; $i += 2) {
        try { if (& $check) { return } } catch { }
        Start-Sleep -Seconds 2
    }
    throw "$what did not start within $seconds seconds."
}

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw 'Run this in PowerShell opened as Administrator (right-click PowerShell > Run as administrator).' }

if ($Uninstall) {
    Step 'Removing the ERP services (data, settings and backups stay)'
    foreach ($s in $ApiService, $DbService) {
        if (Get-Service $s -ErrorAction SilentlyContinue) {
            Stop-Service $s -Force -ErrorAction SilentlyContinue
            sc.exe delete $s | Out-Null
            Write-Host "Removed service $s"
        }
    }
    Unregister-ScheduledTask -TaskName 'ERP nightly backup' -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "Left in place: $InstallDir (delete it yourself only when the data is no longer needed)."
    return
}

# --- Package -----------------------------------------------------------------------------------------------------------
if (-not $Package) {
    $here = Split-Path $PSScriptRoot -Parent
    if (Test-Path (Join-Path $here 'app\Erp.Api.exe')) { $Package = $here }
    elseif (Test-Path (Join-Path $here 'src\Erp.Api')) {
        Step 'Building the package from the source code'
        & (Join-Path $PSScriptRoot 'package.ps1') -NoZip
        $Package = Join-Path $here 'dist\erp-package'
    } else { throw 'Package not found: run this script from the unzipped package folder.' }
}
if (-not (Test-Path (Join-Path $Package 'app\Erp.Api.exe'))) { throw "Not an ERP package (no app\Erp.Api.exe): $Package" }
$version = if (Test-Path (Join-Path $Package 'VERSION.txt')) { (Get-Content (Join-Path $Package 'VERSION.txt') -Raw).Trim() } else { 'unknown' }
Write-Host "Installing ERP $version into $InstallDir"
New-Item -ItemType Directory -Force $InstallDir, $configDir, (Join-Path $InstallDir 'backups') | Out-Null

# The key in src/Erp.Api/appsettings.Development.json: anyone with the source code has it.
$DevFieldKey = 'ZGV2LW9ubHktZmllbGQta2V5LTMyLWJ5dGVzLWxvbmc='

# --- Settings (kept on updates) -----------------------------------------------------------------------------------------
$settings = if (Test-Path $configFile) { Get-Content $configFile -Raw | ConvertFrom-Json } else { $null }
$fresh = $null -eq $settings
if ($fresh) {
    Step 'Creating new passwords and keys'
    $ownerPw = Secret 24; $appPw = Secret 24; $workerPw = Secret 24
    $fieldKey = Secret 32 -Base64; $indexKey = Secret 32 -Base64
    if ($ImportKeysFrom) {
        # Data restored from elsewhere stays readable only with the keys it was encrypted with.
        $old = Get-Content $ImportKeysFrom -Raw | ConvertFrom-Json
        $fieldKey = $old.Security.FieldEncryptionKey; $indexKey = $old.Security.BlindIndexKey
        if (-not $fieldKey -or -not $indexKey) { throw "No Security keys in $ImportKeysFrom" }
        if ($fieldKey -eq $DevFieldKey) {
            Write-Warning ("These are the public development keys (they are in the source code): QID, passport and IBAN numbers are not " +
                "protected with them. Fine for demo data; for a real shop install without -ImportKeysFrom so new keys are made.")
        }
    }
    $conn = { param($user, $pw) "Host=localhost;Port=$DbPort;Database=erp;Username=$user;Password=$pw" }
    $settings = [ordered]@{
        Urls = "http://localhost:$Port"
        Database = [ordered]@{
            OwnerConnectionString = & $conn 'postgres' $ownerPw
            AppConnectionString = & $conn 'erp_app' $appPw
            WorkerConnectionString = & $conn 'erp_worker' $workerPw
            AppRolePassword = $appPw; WorkerRolePassword = $workerPw; MigrateOnStartup = $true
        }
        Jwt = [ordered]@{ SigningKey = Secret 48 }
        Security = [ordered]@{ FieldEncryptionKey = $fieldKey; BlindIndexKey = $indexKey }
        Platform = [ordered]@{ ApiKey = Secret 24 }
        Web = [ordered]@{ Root = 'wwwroot' }
        App = [ordered]@{ PublicUrl = $PublicUrl }
        Install = [ordered]@{ PgBin = ''; ApiService = $ApiService; DbService = $DbService; Version = $version }
    } | ConvertTo-Json -Depth 5 | ConvertFrom-Json
} else {
    Write-Host 'Keeping the existing passwords and keys.'
    if ($ImportDump) { throw 'This computer already has an ERP database. To replace its data use scripts\restore.ps1 instead.' }
}
$ownerPw = ($settings.Database.OwnerConnectionString -split 'Password=')[1]

# --- PostgreSQL -----------------------------------------------------------------------------------------------------------
Step 'Database service'
$initdb = Get-ChildItem 'C:\Program Files\PostgreSQL\*\bin\initdb.exe' -ErrorAction SilentlyContinue |
    Where-Object { [int]($_.Directory.Parent.Name -replace '\D', '') -ge 17 } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not (Get-Service $DbService -ErrorAction SilentlyContinue)) {
    if (-not $initdb) {
        Write-Host 'Installing PostgreSQL 17 (this takes a few minutes)...'
        $override = "--mode unattended --unattendedmodeui none --superpassword $ownerPw --serverport $DbPort --servicename $DbService " +
            "--datadir `"$pgData`" --disable-components pgAdmin,stackbuilder"
        winget install --id PostgreSQL.PostgreSQL.17 --exact --silent --accept-source-agreements --accept-package-agreements --override $override
        if ($LASTEXITCODE -ne 0) { throw 'Installing PostgreSQL failed. Install PostgreSQL 17 from postgresql.org, then run this script again.' }
        $initdb = Get-ChildItem 'C:\Program Files\PostgreSQL\17\bin\initdb.exe' | Select-Object -First 1
    } else {
        if (-not (Test-Path (Join-Path $pgData 'PG_VERSION'))) {
            Write-Host "Creating the ERP database cluster in $pgData (port $DbPort, local connections only)"
            New-Item -ItemType Directory -Force $pgData | Out-Null
            $pwFile = Join-Path $env:TEMP "erp-pw-$(Secret 4).txt"
            Set-Content $pwFile $ownerPw -NoNewline -Encoding ascii
            try {
                & $initdb.FullName -D $pgData -U postgres "--pwfile=$pwFile" -E UTF8 --locale=C -A scram-sha-256 -c listen_addresses=localhost -c "port=$DbPort"
                if ($LASTEXITCODE -ne 0) { throw 'initdb failed' }
            } finally { Remove-Item $pwFile -Force }
        }
        # The service runs as NETWORK SERVICE, which must own its data folder.
        icacls $pgData /grant 'NT AUTHORITY\NetworkService:(OI)(CI)F' /T /Q | Out-Null
        & (Join-Path $initdb.DirectoryName 'pg_ctl.exe') register -N $DbService -D $pgData -S auto -w
        if ($LASTEXITCODE -ne 0) { throw 'Registering the database service failed.' }
    }
}
$pgBin = $initdb.DirectoryName
$settings.Install.PgBin = $pgBin
Start-Service $DbService
$env:PGPASSWORD = $ownerPw
Wait-Until { & (Join-Path $pgBin 'pg_isready.exe') -h localhost -p $DbPort -q; $LASTEXITCODE -eq 0 } 60 'The database'
function Sql([string]$db, [string]$sql) {
    $out = & (Join-Path $pgBin 'psql.exe') -h localhost -p $DbPort -U postgres -d $db -v ON_ERROR_STOP=1 -q -At -c $sql
    if ($LASTEXITCODE -ne 0) { throw "psql failed: $sql" }
    return $out
}
if (-not (Sql 'postgres' "SELECT 1 FROM pg_database WHERE datname = 'erp'")) {
    Sql 'postgres' 'CREATE DATABASE erp' | Out-Null
    if ($ImportDump) {
        Step "Importing $ImportDump"
        # The app's roles must exist before the data (row-level security policies name them).
        Sql 'postgres' "CREATE ROLE erp_app LOGIN PASSWORD '$($settings.Database.AppRolePassword)' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS" | Out-Null
        Sql 'postgres' "CREATE ROLE erp_worker LOGIN PASSWORD '$($settings.Database.WorkerRolePassword)' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS" | Out-Null
        & (Join-Path $pgBin 'pg_restore.exe') -h localhost -p $DbPort -U postgres -d erp --no-owner --role=postgres --exit-on-error $ImportDump
        if ($LASTEXITCODE -ne 0) { throw 'Importing the backup failed.' }
    }
}

# Written now, so a failure later never loses the new keys.
$settings | ConvertTo-Json -Depth 5 | Set-Content $configFile -Encoding utf8
icacls $configDir /inheritance:r /grant:r 'BUILTIN\Administrators:(OI)(CI)F' 'NT AUTHORITY\SYSTEM:(OI)(CI)F' /Q | Out-Null

# --- Program ----------------------------------------------------------------------------------------------------------------
Step 'ERP program and service'
if (Get-Service $ApiService -ErrorAction SilentlyContinue) { Stop-Service $ApiService -Force; Start-Sleep -Seconds 2 }
robocopy (Join-Path $Package 'app') $appDir /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'Copying the program failed.' }
New-Item -ItemType Directory -Force (Join-Path $InstallDir 'scripts'), (Join-Path $InstallDir 'docs') | Out-Null
Copy-Item (Join-Path $Package 'scripts\*') (Join-Path $InstallDir 'scripts') -Force -ErrorAction SilentlyContinue
foreach ($s in 'backup.ps1', 'restore.ps1', 'db-tools.ps1', 'install.ps1') {
    if (Test-Path (Join-Path $PSScriptRoot $s)) { Copy-Item (Join-Path $PSScriptRoot $s) (Join-Path $InstallDir 'scripts') -Force -ErrorAction SilentlyContinue }
}
Copy-Item (Join-Path $Package 'docs\*') (Join-Path $InstallDir 'docs') -Force -ErrorAction SilentlyContinue

if (-not (Get-Service $ApiService -ErrorAction SilentlyContinue)) {
    New-Service -Name $ApiService -DisplayName 'ERP' -StartupType Automatic -DependsOn $DbService `
        -BinaryPathName "`"$appDir\Erp.Api.exe`" --ConfigFile `"$configFile`"" -Description 'Qatar SME ERP: the program behind the till, the back-office and the phones.' | Out-Null
    # Its own account (no password, no rights beyond what is granted below) instead of SYSTEM.
    sc.exe config $ApiService obj= "NT SERVICE\$ApiService" | Out-Null
    # Restart after 10 s, 10 s, then 1 minute if it stops unexpectedly; forget failures after a day.
    sc.exe failure $ApiService reset= 86400 actions= restart/10000/restart/10000/restart/60000 | Out-Null
}
icacls $appDir /grant "NT SERVICE\${ApiService}:(OI)(CI)RX" /T /Q | Out-Null
icacls $configDir /grant "NT SERVICE\${ApiService}:(OI)(CI)R" /Q | Out-Null
if (-not [Diagnostics.EventLog]::SourceExists('Erp.Api')) { New-EventLog -LogName Application -Source 'Erp.Api' }

# A copy started by hand (development, dotnet run) would hold the port.
Get-Process -Name 'Erp.Api' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Service $ApiService
Write-Host 'Waiting for the ERP to start (the first start prepares the database)...'
Wait-Until { (Invoke-WebRequest -UseBasicParsing "http://localhost:$Port/health" -TimeoutSec 5).StatusCode -eq 200 } 240 'The ERP'
Write-Host "The ERP answers on http://localhost:$Port" -ForegroundColor Green

if ($AdminEmail -and $AdminPassword) {
    Step 'First back-office administrator'
    try {
        Invoke-RestMethod -Method Post "http://localhost:$Port/api/platform/admins" -Headers @{ 'X-Platform-Key' = $settings.Platform.ApiKey } `
            -ContentType 'application/json' -Body (@{ email = $AdminEmail; displayName = $AdminEmail; password = $AdminPassword } | ConvertTo-Json) | Out-Null
        Write-Host "Created $AdminEmail" -ForegroundColor Green
    } catch { Write-Host "Not created ($($_.Exception.Message)); it may exist already." -ForegroundColor Yellow }
}

# --- Backups ----------------------------------------------------------------------------------------------------------------
Step 'Nightly backup'
$backup = Join-Path $InstallDir 'scripts\backup.ps1'
$backupArgs = @{ Install = $true; At = $BackupAt; AsSystem = $true; Config = $configFile; Dir = (Join-Path $InstallDir 'backups') }
if ($BackupCopyTo) { $backupArgs.CopyTo = $BackupCopyTo }
& $backup @backupArgs
& $backup -Verify -Config $configFile -Dir (Join-Path $InstallDir 'backups')

# --- Tailscale ---------------------------------------------------------------------------------------------------------------
Step 'Tailscale'
$ts = (Get-Command tailscale -ErrorAction SilentlyContinue).Source
if (-not $ts -and (Test-Path "$env:ProgramFiles\Tailscale\tailscale.exe")) { $ts = "$env:ProgramFiles\Tailscale\tailscale.exe" }
$address = $null
if ($ts) {
    $status = & $ts status --json | ConvertFrom-Json
    if ($status.BackendState -eq 'Running' -and $status.Self.DNSName -like '*.ts.net.') {
        & $ts serve --bg --https=443 "http://localhost:$Port" | Out-Null
        $address = 'https://' + $status.Self.DNSName.TrimEnd('.')
    } else { Write-Host 'Tailscale is installed but not signed in: run "tailscale up", then this script again.' -ForegroundColor Yellow }
} else { Write-Host 'Tailscale is not installed: see docs\Tailscale.md to reach the ERP from phones.' -ForegroundColor Yellow }

# --- Done ---------------------------------------------------------------------------------------------------------------------
Step 'Done'
Write-Host "ERP $version is installed and starts by itself with Windows." -ForegroundColor Green
if ($address) { Write-Host "Open it at:        $address   (back-office: $address/backoffice)" -ForegroundColor Green }
Write-Host "On this computer:  http://localhost:$Port"
Write-Host "Settings and keys: $configFile"
Write-Host "Backups:           $(Join-Path $InstallDir 'backups')$(if ($BackupCopyTo) { " and $BackupCopyTo" }) every night at $BackupAt"
if ($fresh) {
    Write-Host ''
    Write-Host 'IMPORTANT: copy these two keys into your password manager now. Without them a backup restored on another computer' -ForegroundColor Yellow
    Write-Host 'cannot read QID numbers, passports and IBANs:' -ForegroundColor Yellow
    Write-Host "  Security:FieldEncryptionKey = $($settings.Security.FieldEncryptionKey)"
    Write-Host "  Security:BlindIndexKey      = $($settings.Security.BlindIndexKey)"
}
