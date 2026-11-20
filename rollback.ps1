<# Restore the GHFix DLL and manifest backed up by install.ps1.
   No official Rhino.Inside/Revit files or model content are modified.
#>
[CmdletBinding()]
param([int[]]$RevitYears = @(2026))
$ErrorActionPreference = 'Stop'
if (Get-Process -Name Revit -ErrorAction SilentlyContinue) { throw 'Please close all Revit.exe processes before rollback.' }
foreach ($year in $RevitYears) {
    $recordFile = Join-Path $env:LOCALAPPDATA "RhinoInside.GHFix\LastBackup_$year.txt"
    if (!(Test-Path $recordFile)) { throw "No GHFix backup record for $year" }
    $backup = (Get-Content -LiteralPath $recordFile -Raw).Trim()
    if (!(Test-Path $backup)) { throw "Backup directory not found: $backup" }
    $base = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"
    $dest = Join-Path $base 'RhinoInside.GHFix'
    $addin = Join-Path $base 'RhinoInside.GHFix.addin'
    $dll = Join-Path $dest 'RhinoInside.GHFix.dll'
    $oldAddin = Join-Path $backup 'RhinoInside.GHFix.addin'
    $oldDll = Join-Path $backup 'RhinoInside.GHFix.dll'
    $pdb = Join-Path $dest 'RhinoInside.GHFix.pdb'
    $oldPdb = Join-Path $backup 'RhinoInside.GHFix.pdb'
    if (Test-Path $addin) {
        $manifest = Get-Content -LiteralPath $addin -Raw
        if ($manifest -notmatch '<Name>RhinoInside\.GHFix</Name>') { throw "Unexpected manifest: $addin" }
    }
    if (Test-Path $oldDll) { New-Item -ItemType Directory -Force -Path $dest | Out-Null; Copy-Item -LiteralPath $oldDll -Destination $dll -Force }
    elseif (Test-Path $dll) { Remove-Item -LiteralPath $dll -Force }
    if (Test-Path -LiteralPath $oldPdb) { New-Item -ItemType Directory -Force -Path $dest | Out-Null; Copy-Item -LiteralPath $oldPdb -Destination $pdb -Force }
    elseif (Test-Path -LiteralPath $pdb) { Remove-Item -LiteralPath $pdb -Force }
    if (Test-Path $oldAddin) { Copy-Item -LiteralPath $oldAddin -Destination $addin -Force }
    elseif (Test-Path $addin) { Remove-Item -LiteralPath $addin -Force }
    Write-Host "Rolled back GHFix for Revit $year from $backup (official Rhino.Inside unchanged)" -ForegroundColor Green
}
