<#
.SYNOPSIS
  Builds the ERP package for a shop computer: dist\erp-package (and a .zip of it) with the program, the screens and the scripts.

.DESCRIPTION
  The program is published self-contained for 64-bit Windows, so the shop computer needs no .NET or Node. The built web app goes
  into app\wwwroot (the program serves it). Copy the zip to the shop computer (USB stick), unzip it and run
  scripts\install.ps1 as Administrator. The same package updates an installed computer: run install.ps1 again.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\package.ps1
#>
param(
    [string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\erp-package'),
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = (& git -C $root describe --always --dirty 2>$null)
if (-not $version) { $version = 'unknown' }

Write-Host "Packaging ERP $version"
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory -Force "$Out\app", "$Out\scripts", "$Out\docs" | Out-Null

Write-Host 'Building the screens...'
Push-Location (Join-Path $root 'src\Erp.Web')
try { npx ng build; if ($LASTEXITCODE -ne 0) { throw 'ng build failed' } } finally { Pop-Location }

Write-Host 'Building the program...'
dotnet publish (Join-Path $root 'src\Erp.Api\Erp.Api.csproj') -c Release -r win-x64 --self-contained true -o "$Out\app" -p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
# Development settings never go to a shop computer; its own settings are written by install.ps1.
Remove-Item "$Out\app\appsettings.Development.json" -ErrorAction SilentlyContinue

Copy-Item (Join-Path $root 'src\Erp.Web\dist\erp-web\browser') "$Out\app\wwwroot" -Recurse
foreach ($s in 'install.ps1', 'backup.ps1', 'restore.ps1', 'db-tools.ps1') { Copy-Item (Join-Path $PSScriptRoot $s) "$Out\scripts\" }
foreach ($d in 'Shop-Setup.md', 'Backups.md', 'Tailscale.md') { Copy-Item (Join-Path $root "docs\$d") "$Out\docs\" -ErrorAction SilentlyContinue }
Set-Content "$Out\VERSION.txt" "$version $(Get-Date -Format 'yyyy-MM-dd HH:mm')" -Encoding ascii

if (-not $NoZip) {
    $zip = Join-Path (Split-Path $Out -Parent) "erp-package-$version.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path "$Out\*" -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "Package: $zip ($('{0:N0}' -f ((Get-Item $zip).Length / 1MB)) MB)" -ForegroundColor Green
}
Write-Host "Folder:  $Out" -ForegroundColor Green
