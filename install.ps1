# Build and install into the Revit 2024 add-ins folder.
#   powershell -ExecutionPolicy Bypass -File install.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

dotnet build "$root\SheetMerge.Acad" -c Release
if ($LASTEXITCODE -ne 0) { throw 'SheetMerge.Acad build failed' }
dotnet build "$root\RevitSheetsToDwg" -c Release
if ($LASTEXITCODE -ne 0) { throw 'RevitSheetsToDwg build failed' }

$addins = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2024'
$target = Join-Path $addins 'RevitSheetsToDwg'
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item "$root\RevitSheetsToDwg\bin\Release\RevitSheetsToDwg.dll" $target -Force
Copy-Item "$root\SheetMerge.Acad\bin\Release\SheetMerge.Acad.dll" $target -Force
Copy-Item "$root\RevitSheetsToDwg\RevitSheetsToDwg.addin" $addins -Force
Write-Host "Installed: $addins"
