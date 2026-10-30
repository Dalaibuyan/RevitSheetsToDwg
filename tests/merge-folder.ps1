# Merge DWGs from a folder with SheetMerge.Acad (same way the Revit add-in does) and show the console log.
#   powershell -ExecutionPolicy Bypass -File tests\merge-folder.ps1 -Folder tests\real [-Mode LAYOUTS|MODEL] [-Filter "006_*"]
param([Parameter(Mandatory)][string]$Folder, [string]$Mode = 'LAYOUTS', [string]$Filter = '*.dwg', [string]$Output)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$Folder = (Resolve-Path $Folder).Path
$work = Join-Path $PSScriptRoot "out\folder-$Mode"
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $work | Out-Null
if (-not $Output) { $Output = Join-Path $work "Merged.dwg" }

$files = Get-ChildItem $Folder -Filter *.dwg | Where-Object { $_.Name -like $Filter -and $_.Name -match '^\d{3}_' } | Sort-Object Name
$lines = @("OUTPUT=$Output", "MODE=$Mode") + ($files | ForEach-Object { "FILE=" + $_.FullName })
[IO.File]::WriteAllLines("$work\job.txt", $lines, (New-Object Text.UTF8Encoding($false)))

$sm = "$root\SheetMerge.Acad\bin\Release\SheetMerge.Acad.dll" -replace '\\', '/'
[IO.File]::WriteAllText("$work\merge.scr", "(setvar `"SECURELOAD`" 0)`r`n(setvar `"FILEDIA`" 0)`r`n(command `"_.NETLOAD`" `"$sm`")`r`nMERGESHEETS`r`n")
$env:SHEETMERGE_JOB = "$work\job.txt"
$env:SHEETMERGE_TRACE = "$work\trace.txt"
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process 'C:\Program Files\Autodesk\AutoCAD 2020\accoreconsole.exe' -ArgumentList "/s `"$work\merge.scr`"" -NoNewWindow -PassThru -RedirectStandardOutput "$work\console.log"
if (-not $p.WaitForExit(240000)) { Write-Host "TIMEOUT - killing"; $p.Kill() }
Remove-Item Env:SHEETMERGE_JOB
Write-Host ("Files: " + $files.Count + ", time: " + [int]$sw.Elapsed.TotalSeconds + "s")
Write-Host ("Result: " + ((Get-Content "$work\job.txt.result" -ErrorAction SilentlyContinue) -join ' '))
$log = [Text.Encoding]::Unicode.GetString([IO.File]::ReadAllBytes("$work\console.log"))
if ($log -notmatch 'Command') { $log = Get-Content "$work\console.log" -Raw }
($log -replace "`0", "") -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 15
