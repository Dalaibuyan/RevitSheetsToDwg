# Plot one paper-space layout (by index in tab order, 0-based) of a DWG to PNG.
#   powershell -File tests\plot-layout.ps1 -Dwg x.dwg -Index 0 -Png out.png
param([Parameter(Mandatory)][string]$Dwg, [int]$Index = 0, [Parameter(Mandatory)][string]$Png)
$tmp = Join-Path $env:TEMP ("plot_" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
# Layout is chosen by tab order via TestGen's SETTAB, so Cyrillic layout names never appear in the script
$tg = "$PSScriptRoot\TestGen\bin\Release\TestGen.dll" -replace '\\', '/'
$env:TAB_INDEX = $Index + 1
$scr = @"
(setvar "SECURELOAD" 0)
(command "_.NETLOAD" "$tg")
SETTAB
-PLOT
Y

PublishToWeb PNG.pc3
Sun Hi-Res (1600.00 x 1280.00 Pixels)
L
N
E
F
C
Y
monochrome.ctb
Y
N
N
N
$tmp\out.png
N
Y
"@
[IO.File]::WriteAllText("$tmp\p.scr", ($scr -replace "`r?`n", "`r`n") + "`r`n")
$p = Start-Process 'C:\Program Files\Autodesk\AutoCAD 2020\accoreconsole.exe' -ArgumentList "/i `"$Dwg`" /s `"$tmp\p.scr`" /readonly" -NoNewWindow -PassThru -RedirectStandardOutput "$tmp\log.txt"
if (-not $p.WaitForExit(180000)) { $p.Kill() }
if (Test-Path "$tmp\out.png") { Move-Item "$tmp\out.png" $Png -Force; Write-Host "OK $Png" } else { Write-Host "FAILED"; Get-Content "$tmp\log.txt" -Raw | Select-Object -Last 1 }
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
