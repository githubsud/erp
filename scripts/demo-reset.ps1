<#
.SYNOPSIS
  Puts the demo back to a clean state before a customer visit.

.DESCRIPTION
  Full reset (default): rebuilds the local database and seeds "Demo Trading" plus the eight demo businesses again, so dates such as
  "sales today", overdue invoices and expiring documents are relative to today. Takes a few minutes; run it the evening before visits.
  Afterwards a snapshot is saved in scripts\.demo.

  Quick (-Quick): restores the latest snapshot in about a minute (data keeps the dates of the day the snapshot was made).

  Before anything is deleted, the current database is saved in scripts\.demo\before-reset-<time>.dump.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\demo-reset.ps1
  powershell -ExecutionPolicy Bypass -File scripts\demo-reset.ps1 -Quick
#>
param([switch]$Quick, [string]$Api = 'http://localhost:5080', [string]$Container = 'erp-postgres')

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$store = Join-Path $PSScriptRoot '.demo'
New-Item -ItemType Directory -Force $store | Out-Null

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

function Invoke-Psql($sql) {
    docker exec $Container psql -U postgres -d postgres -v ON_ERROR_STOP=1 -q -c $sql | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "psql failed: $sql" }
}

function Save-Dump($path) {
    docker exec $Container pg_dump -U postgres -Fc -f /tmp/erp.dump erp
    if ($LASTEXITCODE -ne 0) { throw 'pg_dump failed' }
    docker cp "${Container}:/tmp/erp.dump" $path | Out-Null
}

function Stop-Api {
    Get-Process -Name 'Erp.Api' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
}

function Start-Api {
    Start-Process -FilePath 'dotnet' -ArgumentList 'run --project src/Erp.Api --launch-profile http' -WorkingDirectory $root -WindowStyle Minimized
    for ($i = 0; $i -lt 120; $i++) {
        try {
            if ((Invoke-WebRequest -UseBasicParsing "$Api/health" -TimeoutSec 3).StatusCode -eq 200) { return }
        } catch { Start-Sleep -Seconds 2 }
    }
    throw "The API did not start at $Api"
}

function Reset-Database {
    Invoke-Psql 'DROP DATABASE IF EXISTS erp WITH (FORCE);'
    Invoke-Psql 'CREATE DATABASE erp;'
}

Step 'Saving the current database'
$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
Save-Dump (Join-Path $store "before-reset-$stamp.dump")
Stop-Api

if ($Quick) {
    $snapshot = Get-ChildItem $store -Filter 'demo-*.dump' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $snapshot) { throw 'No snapshot yet: run a full reset first (without -Quick).' }
    Step "Restoring $($snapshot.Name)"
    Reset-Database
    docker cp $snapshot.FullName "${Container}:/tmp/restore.dump" | Out-Null
    docker exec $Container pg_restore -U postgres -d erp --no-owner --role=postgres /tmp/restore.dump
    if ($LASTEXITCODE -ne 0) { throw 'pg_restore failed' }
    Step 'Starting the API'
    Start-Api
    Write-Host "`nDone. Demo restored from $($snapshot.LastWriteTime.ToString('dd/MM/yyyy HH:mm')) (dates are as of that day)." -ForegroundColor Green
    return
}

Step 'Rebuilding the database'
Reset-Database
Step 'Starting the API (creates the tables)'
Start-Api

Step 'Seeding Demo Trading'
python (Join-Path $PSScriptRoot 'seed-demo.py') --api $Api
if ($LASTEXITCODE -ne 0) { throw 'seed-demo.py failed' }
Step 'Seeding the eight demo businesses'
python (Join-Path $PSScriptRoot 'seed-business-demos.py') --api $Api
if ($LASTEXITCODE -ne 0) { throw 'seed-business-demos.py failed' }

Step 'Saving a snapshot for quick resets'
Save-Dump (Join-Path $store "demo-$stamp.dump")
Get-ChildItem $store -Filter '*.dump' | Sort-Object LastWriteTime -Descending | Select-Object -Skip 6 | Remove-Item

Write-Host "`nDone. Sign in at http://localhost:4200 (company code = business code, owner@<code>.qa, Demo-Password-2026)." -ForegroundColor Green
Write-Host 'Back-office: http://localhost:4200/backoffice (admin@erp.qa). The AI providers (Gemini key) must be entered again after a full reset.' -ForegroundColor Yellow
