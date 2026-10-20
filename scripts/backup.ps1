<#
.SYNOPSIS
  Daily backup of the ERP database, with a test restore and an optional second copy.

.DESCRIPTION
  Saves the whole database (all companies) as backups\erp-<date>-<time>.dump with a SHA-256 checksum next to it, keeps the
  newest -Keep backups and deletes older ones. With -CopyTo the backup is also copied to a second place (a USB disk or a synced
  folder such as OneDrive) and pruned there too: a backup on the same disk as the database does not survive a broken disk.

  -Verify restores the new backup into a scratch database (erp_restore_check), checks that companies, users and ledger lines
  came back, and drops it again. A backup that has never been restored is only a hope.

  -Install registers a Windows scheduled task that runs this script every night (with -Verify, and -CopyTo when given).

  IMPORTANT: QID, passport, IBAN and API keys are stored encrypted. A restore is only readable with the same
  Encryption:FieldEncryptionKey the API uses. Keep a copy of that key somewhere safe and separate from the backups.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\backup.ps1 -Verify
  powershell -ExecutionPolicy Bypass -File scripts\backup.ps1 -CopyTo "$env:OneDrive\ERP backups" -Verify
  powershell -ExecutionPolicy Bypass -File scripts\backup.ps1 -Install -At 23:30 -CopyTo E:\ERP-backups
#>
param(
    [string]$Dir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'backups'),
    [string]$CopyTo,
    [int]$Keep = 14,
    [switch]$Verify,
    [switch]$Install,
    [string]$At = '23:30',
    [string]$Container = 'erp-postgres',
    [string]$Database = 'erp'
)

$ErrorActionPreference = 'Stop'

if ($Install) {
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Verify -Dir `"$Dir`" -Keep $Keep"
    if ($CopyTo) { $arguments += " -CopyTo `"$CopyTo`"" }
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments
    $trigger = New-ScheduledTaskTrigger -Daily -At $At
    # Runs at the next chance when the PC was off at that time.
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 2)
    Register-ScheduledTask -TaskName 'ERP nightly backup' -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
    Write-Host "Scheduled: 'ERP nightly backup' every day at $At." -ForegroundColor Green
    return
}

function Psql([string]$db, [string]$sql) {
    $out = docker exec $Container psql -U postgres -d $db -v ON_ERROR_STOP=1 -q -At -c "SET client_min_messages TO warning" -c $sql
    if ($LASTEXITCODE -ne 0) { throw "psql failed on ${db}: $sql" }
    return $out
}

function Prune([string]$folder) {
    Get-ChildItem $folder -Filter 'erp-*.dump' | Sort-Object Name -Descending | Select-Object -Skip $Keep | ForEach-Object {
        Remove-Item $_.FullName
        Remove-Item "$($_.FullName).sha256" -ErrorAction SilentlyContinue
    }
}

New-Item -ItemType Directory -Force $Dir | Out-Null
$name = "erp-$(Get-Date -Format 'yyyyMMdd-HHmmss').dump"
$file = Join-Path $Dir $name
$log = Join-Path $Dir 'backup.log'

try {
    docker exec $Container pg_dump -U postgres -Fc -Z 6 -f /tmp/backup.dump $Database
    if ($LASTEXITCODE -ne 0) { throw 'pg_dump failed' }
    docker cp "${Container}:/tmp/backup.dump" $file | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'copying the backup out of the container failed' }
    docker exec $Container rm -f /tmp/backup.dump | Out-Null
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash
    Set-Content "$file.sha256" "$hash  $name" -Encoding ascii
    $size = '{0:N1} MB' -f ((Get-Item $file).Length / 1MB)

    $checked = ''
    if ($Verify) {
        $scratch = 'erp_restore_check'
        Psql 'postgres' "DROP DATABASE IF EXISTS $scratch WITH (FORCE);" | Out-Null
        Psql 'postgres' "CREATE DATABASE $scratch;" | Out-Null
        try {
            docker cp $file "${Container}:/tmp/check.dump" | Out-Null
            docker exec $Container pg_restore -U postgres -d $scratch --no-owner --role=postgres --exit-on-error /tmp/check.dump
            if ($LASTEXITCODE -ne 0) { throw 'test restore failed: the backup cannot be restored' }
            # The live database may have grown since the dump; the restored copy must have data and never more than live.
            $query = "SELECT (SELECT count(*) FROM core.tenant) || ',' || (SELECT count(*) FROM core.app_user) || ',' || (SELECT count(*) FROM fin.journal_line);"
            $restored = (Psql $scratch $query) -split ','
            $live = (Psql $Database $query) -split ','
            if ([int64]$restored[0] -eq 0) { throw 'test restore has no companies' }
            for ($i = 0; $i -lt 3; $i++) {
                if ([int64]$restored[$i] -gt [int64]$live[$i]) { throw "test restore does not match the live database ($($restored -join '/') vs $($live -join '/'))" }
            }
            $checked = " - restore checked: $($restored[0]) companies, $($restored[1]) users, $($restored[2]) ledger lines"
        } finally {
            docker exec $Container rm -f /tmp/check.dump | Out-Null
            Psql 'postgres' "DROP DATABASE IF EXISTS $scratch WITH (FORCE);" | Out-Null
        }
    }

    Prune $Dir
    if ($CopyTo) {
        New-Item -ItemType Directory -Force $CopyTo | Out-Null
        Copy-Item $file, "$file.sha256" $CopyTo
        if ((Get-FileHash (Join-Path $CopyTo $name) -Algorithm SHA256).Hash -ne $hash) { throw "the copy in $CopyTo is damaged" }
        Prune $CopyTo
        $checked += " - copied to $CopyTo"
    }

    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm') OK $name ($size)$checked"
    Add-Content $log $line
    Write-Host $line -ForegroundColor Green
} catch {
    Add-Content $log "$(Get-Date -Format 'yyyy-MM-dd HH:mm') FAILED $name : $_"
    Write-Host "Backup FAILED: $_" -ForegroundColor Red
    exit 1
}
