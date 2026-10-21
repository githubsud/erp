# Shared by backup.ps1 and restore.ps1: where the ERP database is and how to dump, restore and query it.
# An installed computer (install.ps1) has its own PostgreSQL service described in C:\ERP\config\erp.json;
# a development laptop has the erp-postgres Docker container.

function Get-ErpDatabase([string]$Config, [switch]$Docker, [string]$Container = 'erp-postgres') {
    if (-not $Docker -and $Config -and (Test-Path $Config)) {
        $settings = Get-Content $Config -Raw | ConvertFrom-Json
        $parts = @{}
        foreach ($pair in $settings.Database.OwnerConnectionString.Split(';')) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $parts[$kv[0].Trim().ToLowerInvariant()] = $kv[1].Trim() }
        }
        return [pscustomobject]@{
            Native = $true; Host = $parts['host']; Port = $parts['port']; User = $parts['username']; Password = $parts['password']
            Bin = $settings.Install.PgBin; ApiService = $settings.Install.ApiService; Container = $null
        }
    }
    return [pscustomobject]@{ Native = $false; Container = $Container; ApiService = $null }
}

function Use-ErpPassword($db) { if ($db.Native) { $env:PGPASSWORD = $db.Password } }

function Invoke-ErpPsql($db, [string]$name, [string]$sql) {
    Use-ErpPassword $db
    if ($db.Native) {
        $out = & (Join-Path $db.Bin 'psql.exe') -h $db.Host -p $db.Port -U $db.User -d $name -v ON_ERROR_STOP=1 -q -At -c "SET client_min_messages TO warning" -c $sql
    } else {
        $out = docker exec $db.Container psql -U postgres -d $name -v ON_ERROR_STOP=1 -q -At -c "SET client_min_messages TO warning" -c $sql
    }
    if ($LASTEXITCODE -ne 0) { throw "psql failed on ${name}: $sql" }
    return $out
}

function Invoke-ErpDump($db, [string]$name, [string]$file) {
    Use-ErpPassword $db
    if ($db.Native) {
        & (Join-Path $db.Bin 'pg_dump.exe') -h $db.Host -p $db.Port -U $db.User -Fc -Z 6 -f $file $name
        if ($LASTEXITCODE -ne 0) { throw 'pg_dump failed' }
        return
    }
    docker exec $db.Container pg_dump -U postgres -Fc -Z 6 -f /tmp/backup.dump $name
    if ($LASTEXITCODE -ne 0) { throw 'pg_dump failed' }
    docker cp "$($db.Container):/tmp/backup.dump" $file | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'copying the backup out of the container failed' }
    docker exec $db.Container rm -f /tmp/backup.dump | Out-Null
}

function Invoke-ErpRestore($db, [string]$name, [string]$file, [string]$failure = 'pg_restore failed') {
    Use-ErpPassword $db
    if ($db.Native) {
        & (Join-Path $db.Bin 'pg_restore.exe') -h $db.Host -p $db.Port -U $db.User -d $name --no-owner --role=postgres --exit-on-error $file
        if ($LASTEXITCODE -ne 0) { throw $failure }
        return
    }
    docker cp $file "$($db.Container):/tmp/restore.dump" | Out-Null
    try {
        docker exec $db.Container pg_restore -U postgres -d $name --no-owner --role=postgres --exit-on-error /tmp/restore.dump
        if ($LASTEXITCODE -ne 0) { throw $failure }
    } finally {
        docker exec $db.Container rm -f /tmp/restore.dump | Out-Null
    }
}
