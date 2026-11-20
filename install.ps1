<# GHFix 0.13 installer (PowerShell 5.1). Revit must be closed.
   Builds from source using the installed Revit API; backs up active GHFix before replacement.
   Does not change official Rhino.Inside add-ins or Grasshopper plugins.
#>
[CmdletBinding()]
param([int[]]$RevitYears = @(2026),
      [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
      [ValidateSet('auto','net8.0-windows','net10.0-windows')][string]$Runtime = 'auto')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (Get-Process -Name Revit -ErrorAction SilentlyContinue) { throw 'Please close all Revit.exe processes before GHFix install.' }

function Get-Target([int]$year, [string]$runtime) {
    if ($year -lt 2025 -or $year -gt 2027) { throw 'This beta supports Revit 2025-2027 only.' }
    $path = "C:\Program Files\Autodesk\Revit $year"
    $exe = Join-Path $path 'Revit.exe'
    $api = Join-Path $path 'RevitAPIUI.dll'
    if (!(Test-Path $exe) -or !(Test-Path $api)) { throw "Revit files missing: $path" }
    $fv = (Get-Item $exe).VersionInfo.FileVersion
    $match = [regex]::Match($fv, '^(\d+)\.(\d+)')
    if (!$match.Success) { throw "Cannot read Revit FileVersion: $fv" }
    $major = [int]$match.Groups[1].Value
    $minor = [int]$match.Groups[2].Value
    if ($major -ne ($year-2000) -and $major -ne $year) { throw "Revit version mismatch: $year / $fv" }
    $tfm = if ($runtime -ne 'auto') { $runtime } elseif ($year -ge 2027 -or ($year -eq 2026 -and $minor -ge 5)) { 'net10.0-windows' } else { 'net8.0-windows' }
    return @{ TFM=$tfm; Version=$fv }
}

foreach ($year in $RevitYears) {
    $target = Get-Target $year $Runtime
    $tfm = $target.TFM
    Write-Host "Revit $year ($($target.Version)), build: $tfm" -ForegroundColor Cyan

    $project = Join-Path $PSScriptRoot 'src\GHFix.csproj'
    if (!(Test-Path $project)) { throw "Project not found: $project" }
    if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet SDK not found.' }
    $out = Join-Path $PSScriptRoot "out\$year-$tfm\$Configuration"
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    & dotnet build $project -c $Configuration -o $out "-p:RevitYear=$year" "-p:TargetFramework=$tfm" '-p:Platform=x64'
    if ($LASTEXITCODE -ne 0) { throw "Build failed for Revit $year. Existing GHFix not changed." }
    $dll = Join-Path $out 'RhinoInside.GHFix.dll'
    if (!(Test-Path $dll)) { throw 'Compiled DLL was not found.' }

    $base = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"
    $dest = Join-Path $base 'RhinoInside.GHFix'
    $addin = Join-Path $base 'RhinoInside.GHFix.addin'
    $installedDll = Join-Path $dest 'RhinoInside.GHFix.dll'

    # Back up only this GHFix. Never modify official Rhino.Inside add-ins.
    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss_fff'
    $backup = Join-Path $env:LOCALAPPDATA "RhinoInside.GHFix\Backups\$stamp\$year"
    New-Item -ItemType Directory -Force -Path $backup | Out-Null
    if (Test-Path $addin) {
        $oldManifest = Get-Content -LiteralPath $addin -Raw
        if ($oldManifest -notmatch '<Name>RhinoInside\.GHFix</Name>') { throw "Refusing to overwrite unexpected manifest: $addin" }
        Copy-Item -LiteralPath $addin -Destination (Join-Path $backup 'RhinoInside.GHFix.addin') -Force
    }
    if (Test-Path $installedDll) {
        Copy-Item -LiteralPath $installedDll -Destination (Join-Path $backup 'RhinoInside.GHFix.dll') -Force
    }
    $installedPdb = Join-Path $dest 'RhinoInside.GHFix.pdb'
    if (Test-Path -LiteralPath $installedPdb) {
        Copy-Item -LiteralPath $installedPdb -Destination (Join-Path $backup 'RhinoInside.GHFix.pdb') -Force
    }
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item -LiteralPath $dll -Destination $installedDll -Force
    $pdb = Join-Path $out 'RhinoInside.GHFix.pdb'
    if (Test-Path -LiteralPath $pdb) { Copy-Item -LiteralPath $pdb -Destination $installedPdb -Force }
    elseif (Test-Path -LiteralPath $installedPdb) { Remove-Item -LiteralPath $installedPdb -Force }
    $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>RhinoInside.GHFix</Name>
    <Assembly>$installedDll</Assembly>
    <AddInId>cd182f3a-8baf-481a-9ccc-6cc695681054</AddInId>
    <FullClassName>RhinoInside.GHFix.App</FullClassName>
    <VendorId>GHFX</VendorId>
    <VendorDescription>Independent Rhino.Inside Grasshopper launch helper (v0.13 beta)</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
    Set-Content -LiteralPath $addin -Value $xml -Encoding UTF8
    $recordFile = Join-Path $env:LOCALAPPDATA "RhinoInside.GHFix\LastBackup_$year.txt"
    Set-Content -LiteralPath $recordFile -Value $backup -Encoding UTF8
    Write-Host "Installed GHFix 0.13 for Revit $year : $addin" -ForegroundColor Green
    Write-Host "Backup: $backup" -ForegroundColor Yellow
}
Write-Host 'Restart Revit. Use GHFix > Grasshopper safe start. Do NOT test the official GH button yet.' -ForegroundColor Yellow
