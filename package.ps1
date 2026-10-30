# Build the add-in for Revit 2024-2027 and pack an installable zip into dist\.
#   powershell -ExecutionPolicy Bypass -File package.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$years = 2024, 2025, 2026, 2027

dotnet build "$root\SheetMerge.Acad" -c Release
if ($LASTEXITCODE -ne 0) { throw 'SheetMerge.Acad (net48) build failed' }
dotnet build "$root\SheetMerge.Acad" -c Release -p:AcadNet8=true
if ($LASTEXITCODE -ne 0) { throw 'SheetMerge.Acad (net8) build failed' }
foreach ($y in $years) {
    dotnet build "$root\RevitSheetsToDwg" -c Release -p:RevitVersion=$y
    if ($LASTEXITCODE -ne 0) { throw "RevitSheetsToDwg $y build failed" }
}

$name = 'RevitSheetsToDwg_Revit2024-2027'
$pkg = "$root\dist\$name"
Remove-Item $pkg -Recurse -Force -ErrorAction SilentlyContinue
foreach ($y in $years) {
    $dir = "$pkg\Revit$y\RevitSheetsToDwg"
    New-Item -ItemType Directory "$dir\acad2025" -Force | Out-Null
    Copy-Item "$root\RevitSheetsToDwg\bin\Release\R$y\RevitSheetsToDwg.dll" $dir
    Copy-Item "$root\SheetMerge.Acad\bin\Release\SheetMerge.Acad.dll" $dir
    Copy-Item "$root\SheetMerge.Acad\bin\Release\acad2025\SheetMerge.Acad.dll" "$dir\acad2025"
    Copy-Item "$root\RevitSheetsToDwg\RevitSheetsToDwg.addin" "$pkg\Revit$y"
}

$bat = @'
@echo off
rem Installs the "Sheets -> Layouts" / "Sheets -> Model space" add-in into every installed Revit 2024-2027.
rem Close Revit before running.
set "FOUND=0"
for %%Y in (2024 2025 2026 2027) do call :install %%Y
if "%FOUND%"=="0" echo No Revit 2024-2027 found in "%ProgramFiles%\Autodesk".
pause
exit /b 0

:install
if not exist "%ProgramFiles%\Autodesk\Revit %1\Revit.exe" exit /b 0
set "FOUND=1"
set "DEST=%APPDATA%\Autodesk\Revit\Addins\%1"
xcopy /E /I /Y /Q "%~dp0Revit%1\RevitSheetsToDwg" "%DEST%\RevitSheetsToDwg" >nul || goto fail
copy /Y "%~dp0Revit%1\RevitSheetsToDwg.addin" "%DEST%\" >nul || goto fail
echo Revit %1: installed to %DEST%
exit /b 0
:fail
echo Revit %1: install FAILED - close Revit %1 and run again.
exit /b 0
'@
[IO.File]::WriteAllText("$pkg\install.bat", ($bat -replace "`r?`n", "`r`n"))
Copy-Item "$root\README_package.txt" "$pkg\README.txt"

$zip = "$root\dist\$name.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$pkg\*" -DestinationPath $zip
Write-Host "Package: $zip"
