<#
.SYNOPSIS
  Puts the ERP database back to a backup made by backup.ps1.

.DESCRIPTION
  Everything entered after the backup is lost, so the script first saves the current database as
  backups\before-restore-<time>.dump (skip with -NoSafetyCopy only when the database is broken), checks the backup's
  SHA-256 checksum, stops the API, replaces the database and starts the API again (-NoStart to leave it stopped).

  Works on an installed computer (its own PostgreSQL service and the ErpApi service, from C:\ERP\config\erp.json) and on a
  development laptop (Docker container erp-postgres, -Docker to force it).

  The API must run with the same Security:FieldEncryptionKey and Security:BlindIndexKey as when the backup was made, or QID, passport, IBAN and
  API keys cannot be read.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\restore.ps1                     # newest backup in backups\
  powershell -ExecutionPolicy Bypass -File scripts\restore.ps1 -File E:\ERP-backups\erp-20261020-233000.dump
#>
param(
    [string]$File,
    [string]$Dir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'backups'),
    [switch]$Force,
    [switch]$NoSafetyCopy,
    [switch]$NoStart,
    [string]$Container = 'erp-postgres',
    [string]$Database = 'erp',
    [string]$Api = 'http://localhost:5080',
    [string]$Config = 'C:\ERP\config\erp.json',
    [switch]$Docker
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'db-tools.ps1')
$db = Get-ErpDatabase -Config $Config -Docker:$Docker -Container $Container

if (-not $File) {
    $latest = Get-ChildItem $Dir -Filter 'erp-*.dump' -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
    if (-not $latest) { throw "No backups found in $Dir" }
    $File = $latest.FullName
}
if (-not (Test-Path $File)) { throw "Backup not found: $File" }

$sum = "$File.sha256"
if (Test-Path $sum) {
    $expected = ((Get-Content $sum -Raw) -split '\s+')[0]
    if ((Get-FileHash $File -Algorithm SHA256).Hash -ne $expected) { throw "The backup is damaged (checksum does not match): $File" }
    Write-Host 'Checksum OK.'
} else {
    Write-Host 'No checksum file next to the backup; restoring without the check.' -ForegroundColor Yellow
}

$when = (Get-Item $File).LastWriteTime.ToString('dd/MM/yyyy HH:mm')
if (-not $Force) {
    Write-Host "This replaces ALL data with the backup from $when. Anything entered after that is lost." -ForegroundColor Yellow
    if ((Read-Host 'Type RESTORE to continue') -ne 'RESTORE') { Write-Host 'Cancelled.'; return }
}

function Psql([string]$sql) { Invoke-ErpPsql $db 'postgres' $sql | Out-Null }

if (-not $NoSafetyCopy) {
    New-Item -ItemType Directory -Force $Dir | Out-Null
    $safety = Join-Path $Dir "before-restore-$(Get-Date -Format 'yyyyMMdd-HHmmss').dump"
    try { Invoke-ErpDump $db $Database $safety }
    catch { throw 'Could not save the current database first (use -NoSafetyCopy if it is broken).' }
    Write-Host "Current database saved as $safety"
}

$service = if ($db.ApiService) { Get-Service $db.ApiService -ErrorAction SilentlyContinue } else { $null }
if ($service) { Stop-Service $service.Name -Force }
else { Get-Process -Name 'Erp.Api' -ErrorAction SilentlyContinue | Stop-Process -Force }
Start-Sleep -Seconds 2

Psql "DROP DATABASE IF EXISTS $Database WITH (FORCE);"
Psql "CREATE DATABASE $Database;"
Invoke-ErpRestore $db $Database $File 'pg_restore failed. The safety copy can be restored with: restore.ps1 -File <before-restore dump>'
Write-Host "Database restored from $when." -ForegroundColor Green

if (-not $NoStart) {
    if ($service) { Start-Service $service.Name }
    else { Start-Process -FilePath 'dotnet' -ArgumentList 'run --project src/Erp.Api --launch-profile http' -WorkingDirectory $root -WindowStyle Minimized }
    for ($i = 0; $i -lt 90; $i++) {
        try {
            if ((Invoke-WebRequest -UseBasicParsing "$Api/health" -TimeoutSec 3).StatusCode -eq 200) { Write-Host 'API started.' -ForegroundColor Green; return }
        } catch { Start-Sleep -Seconds 2 }
    }
    Write-Host "The API did not answer at $Api; start it by hand." -ForegroundColor Yellow
}
