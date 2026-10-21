<#
.SYNOPSIS
  Makes the ERP on this computer reachable from your other devices (phones, the shop till, your laptop at home)
  at a private https address, through Tailscale. Nothing is opened to the public internet.

.DESCRIPTION
  -Install   installs Tailscale with winget (Windows asks for permission once).
  (default)  signs this computer in (a browser window opens the first time), builds the web app when it is missing,
             and publishes http://localhost:5080 (the API, which also serves the built app) as
             https://<computer>.<tailnet>.ts.net. Prints the address to open on the phones.
  -Build     rebuilds the web app first (after an update).
  -Off       stops publishing the ERP (Tailscale stays installed).

  Before the first run, in the Tailscale admin console (login.tailscale.com/admin/dns) switch on MagicDNS and
  "HTTPS Certificates": phones only install the app from an https address.

  Every device that should open the ERP needs the Tailscale app, signed in to the same account (or invited to it).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\tailscale.ps1 -Install
  powershell -ExecutionPolicy Bypass -File scripts\tailscale.ps1
  powershell -ExecutionPolicy Bypass -File scripts\tailscale.ps1 -Off
#>
param(
    [switch]$Install,
    [switch]$Build,
    [switch]$Off,
    [int]$Port = 5080
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

function Find-Tailscale {
    $cmd = Get-Command tailscale -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $default = Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'
    if (Test-Path $default) { return $default }
    return $null
}

if ($Install) {
    if (Find-Tailscale) { Write-Host 'Tailscale is already installed.' -ForegroundColor Green; return }
    winget install --id Tailscale.Tailscale --exact --accept-source-agreements --accept-package-agreements
    if ($LASTEXITCODE -ne 0) { throw 'Installing Tailscale failed. Download it from https://tailscale.com/download/windows instead.' }
    Write-Host 'Tailscale installed. Now run this script again without -Install.' -ForegroundColor Green
    return
}

$ts = Find-Tailscale
if (-not $ts) { throw 'Tailscale is not installed. Run: scripts\tailscale.ps1 -Install' }

if ($Off) {
    & $ts serve reset
    Write-Host 'The ERP is no longer published on Tailscale.' -ForegroundColor Yellow
    return
}

# Signed in? "tailscale up" opens the browser for the first sign-in and returns at once when already connected.
$status = & $ts status --json | ConvertFrom-Json
if ($status.BackendState -ne 'Running') {
    Write-Host 'Signing this computer in to Tailscale (a browser window opens)...'
    & $ts up
    if ($LASTEXITCODE -ne 0) { throw 'Tailscale sign-in failed.' }
    $status = & $ts status --json | ConvertFrom-Json
}
$dns = $status.Self.DNSName.TrimEnd('.')
if (-not $dns -or $dns -notlike '*.ts.net') {
    throw 'MagicDNS is off. Switch on MagicDNS and HTTPS Certificates at https://login.tailscale.com/admin/dns, then run this again.'
}

# The API serves the built app from src\Erp.Web\dist (see Web:Root in Program.cs).
$web = Join-Path $root 'src\Erp.Web'
if ($Build -or -not (Test-Path (Join-Path $web 'dist\erp-web\browser\index.html'))) {
    Write-Host 'Building the web app...'
    Push-Location $web
    try { npx ng build; if ($LASTEXITCODE -ne 0) { throw 'ng build failed' } } finally { Pop-Location }
}

try {
    Invoke-WebRequest -UseBasicParsing "http://localhost:$Port/health" -TimeoutSec 3 | Out-Null
} catch {
    Write-Host "The ERP is not running on port $Port yet. Start it (dotnet run --project src/Erp.Api); the address below works once it runs." -ForegroundColor Yellow
}

# Runs in the background and survives restarts until "-Off".
& $ts serve --bg --https=443 "http://localhost:$Port"
if ($LASTEXITCODE -ne 0) {
    throw 'Publishing failed. If it mentions certificates, switch on HTTPS Certificates at https://login.tailscale.com/admin/dns.'
}

Write-Host ''
Write-Host "The ERP is at:  https://$dns" -ForegroundColor Green
Write-Host 'Open it on any device signed in to Tailscale. On a phone: open it in Chrome (Android) or Safari (iPhone), then "Install the app" / "Add to Home Screen".'
