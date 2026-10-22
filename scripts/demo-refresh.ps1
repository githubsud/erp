<#
.SYNOPSIS
  Copies the demo businesses from the development copy into the installed ERP, so the visit demo has today's dates.

.DESCRIPTION
  Run as Administrator on the laptop that has both the development copy (Docker) and the installed ERP (C:\ERP).
  Takes the latest demo snapshot made by demo-reset.ps1 (-Fresh makes a new one first, a few minutes) and puts it into
  the installed ERP with restore.ps1, which saves the installed data first as C:\ERP\backups\before-restore-<time>.dump.

  The AI settings of the back-office (Gemini and Groq keys, which provider is on and the default) are kept.

  Only for an installed ERP that holds demo data: it must use the development encryption keys (installed with
  -ImportKeysFrom), otherwise the script stops. A real shop's computer has its own keys and is never touched.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\demo-refresh.ps1 -Fresh
  powershell -ExecutionPolicy Bypass -File scripts\demo-refresh.ps1          # latest snapshot in scripts\.demo
#>
param([switch]$Fresh, [string]$Snapshot, [string]$Config = 'C:\ERP\config\erp.json', [string]$Api = 'http://localhost:5080')

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$store = Join-Path $PSScriptRoot '.demo'
. (Join-Path $PSScriptRoot 'db-tools.ps1')

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw 'Run this from a PowerShell window opened with "Run as administrator".' }
if (-not (Test-Path $Config)) { throw "No installed ERP found ($Config)." }

$settings = Get-Content $Config -Raw | ConvertFrom-Json
$dev = Get-Content (Join-Path $root 'src\Erp.Api\appsettings.Development.json') -Raw | ConvertFrom-Json
if ($settings.Security.FieldEncryptionKey -ne $dev.Security.FieldEncryptionKey -or $settings.Security.BlindIndexKey -ne $dev.Security.BlindIndexKey) {
    throw 'The installed ERP has its own encryption keys, so it is not a demo copy. Nothing was changed.'
}

if ($Fresh) {
    Step 'Making a fresh demo in the development copy'
    & (Join-Path $PSScriptRoot 'demo-reset.ps1')
}
if (-not $Snapshot) {
    $latest = Get-ChildItem $store -Filter 'demo-*.dump' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $latest) { throw 'No demo snapshot yet: run with -Fresh.' }
    $Snapshot = $latest.FullName
}
$made = (Get-Item $Snapshot).LastWriteTime
if ($made.Date -ne (Get-Date).Date) {
    Write-Host "The snapshot is from $($made.ToString('dd/MM/yyyy')); its 'today' figures show that day. Use -Fresh for today's dates." -ForegroundColor Yellow
}

$db = Get-ErpDatabase -Config $Config
function Psql([string]$sql) { Invoke-ErpPsql $db 'erp' $sql }

# The installed program migrates its database on start, so the database's newest migration is the program's.
# A snapshot made by newer code would not start under it (the migrator refuses), so the program is updated first.
$demoVersions = & (Join-Path $db.Bin 'pg_restore.exe') --data-only --table=schema_migrations -f - $Snapshot |
    Where-Object { $_ -match '^\d+\t' } | ForEach-Object { [int]($_ -split "`t")[0] }
function Missing-Versions { $have = @(Psql 'SELECT version FROM public.schema_migrations' | ForEach-Object { [int]$_ }); @($demoVersions | Where-Object { $_ -notin $have }) }
if ((Missing-Versions).Count -gt 0) {
    Step "The installed ERP is older than the demo (it lacks V$('{0:D4}' -f (Missing-Versions)[0])): updating it from the source code first"
    & (Join-Path $PSScriptRoot 'install.ps1')
    if ((Missing-Versions).Count -gt 0) { throw 'The installed ERP is still older than the demo. Nothing was changed.' }
}

Step 'Keeping the AI settings'
$ai = Psql "SELECT code, coalesce(api_key, ''), is_enabled, is_default, daily_limit FROM ai.provider ORDER BY code" | ForEach-Object {
    $f = $_ -split '\|'
    [pscustomobject]@{ Code = $f[0]; Key = $f[1]; Enabled = $f[2] -eq 't'; Default = $f[3] -eq 't'; Limit = [int]$f[4] }
}
Write-Host (($ai | ForEach-Object { "$($_.Code): " + $(if ($_.Key) { 'key' } else { 'no key' }) + $(if ($_.Enabled) { ', on' } else { ', off' }) }) -join ' | ')

Step "Loading the demo from $($made.ToString('dd/MM/yyyy HH:mm'))"
& (Join-Path $PSScriptRoot 'restore.ps1') -File $Snapshot -Force -NoStart -Config $Config -Dir (Join-Path (Split-Path (Split-Path $Config -Parent) -Parent) 'backups')

$sql = @('UPDATE ai.provider SET is_default = false;')
foreach ($p in $ai) {
    $key = if ($p.Key) { "'" + $p.Key.Replace("'", "''") + "'" } else { 'NULL' }
    $sql += "UPDATE ai.provider SET api_key = $key, is_enabled = $($p.Enabled.ToString().ToLower()), daily_limit = $($p.Limit) WHERE code = '$($p.Code)';"
}
$default = $ai | Where-Object Default | Select-Object -First 1
if ($default) { $sql += "UPDATE ai.provider SET is_default = true WHERE code = '$($default.Code)';" }
else { $sql = $sql[1..($sql.Count - 1)] }
Psql ($sql -join ' ') | Out-Null

Step 'Starting the ERP'
Start-Service $db.ApiService
for ($i = 0; $i -lt 90; $i++) {
    try {
        if ((Invoke-WebRequest -UseBasicParsing "$Api/health" -TimeoutSec 3).StatusCode -eq 200) {
            Write-Host "`nDone. The demo businesses have the data of $($made.ToString('dd/MM/yyyy')); AI settings kept." -ForegroundColor Green
            return
        }
    } catch { Start-Sleep -Seconds 2 }
}
Write-Host "The ERP did not answer at $Api. The previous data is in C:\ERP\backups\before-restore-*.dump (restore.ps1 -File ...)." -ForegroundColor Yellow
