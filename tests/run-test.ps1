# Test: generate sample sheets -> merge (LAYOUTS or MODEL) -> plot result to PNG.
#   powershell -ExecutionPolicy Bypass -File tests\run-test.ps1 [-Mode LAYOUTS|MODEL]
param([string]$Mode = 'LAYOUTS')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$out = Join-Path $PSScriptRoot "out\$Mode"
$accore = 'C:\Program Files\Autodesk\AutoCAD 2020\accoreconsole.exe'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$out\sheets" | Out-Null

dotnet build "$PSScriptRoot\TestGen" -c Release | Out-Null
dotnet build "$root\SheetMerge.Acad" -c Release | Out-Null

function Run-Accore([string]$script, [string]$dwg) {
    $scr = Join-Path $out ("s" + [guid]::NewGuid().ToString('N') + ".scr")
    [IO.File]::WriteAllText($scr, ($script -replace "`r?`n", "`r`n") + "`r`n")
    $args = if ($dwg) { "/i `"$dwg`" /s `"$scr`"" } else { "/s `"$scr`"" }
    $log = "$scr.log"
    Start-Process $accore -ArgumentList $args -NoNewWindow -Wait -RedirectStandardOutput $log
    ((Get-Content $log -Raw) -replace "`0", "")
}

$tg = "$PSScriptRoot\TestGen\bin\Release\TestGen.dll" -replace '\\', '/'
$sm = "$root\SheetMerge.Acad\bin\Release\SheetMerge.Acad.dll" -replace '\\', '/'
$result = "$out\Merged.dwg"
@(
    "OUTPUT=$result", "MODE=$Mode",
    "FILE=$out\sheets\001_A101 - Plan.dwg",
    "FILE=$out\sheets\002_A102 - Detail.dwg",
    "FILE=$out\sheets\003_A103 - Plan2.dwg"
) | Set-Content "$out\job.txt" -Encoding UTF8

$env:TEST_OUT = "$out\sheets"
$env:SHEETMERGE_JOB = "$out\job.txt"
Run-Accore @"
(setvar "SECURELOAD" 0)
(setvar "FILEDIA" 0)
(command "_.NETLOAD" "$tg")
MAKETESTSHEETS
(command "_.NETLOAD" "$sm")
MERGESHEETS
"@ | Out-Null
Remove-Item Env:SHEETMERGE_JOB
Write-Host ("Merge result: " + ((Get-Content "$out\job.txt.result") -join ' '))

# Plot: model space + each layout
$layouts = if ($Mode -eq 'LAYOUTS') { @('A101 - Plan', 'A102 - Detail', 'A103 - Plan2') } else { @() }
foreach ($tab in @('Model') + $layouts) {
    $png = Join-Path $out (($tab -replace '[^A-Za-z0-9]', '_') + '.png')
    $isModel = $tab -eq 'Model'
    $lw = if ($isModel) { "Y" } else { "Y`nN`nN`nN" } # lineweights / scale lw / paper space first / hide
    $plotLog = Run-Accore @"
(setvar "CTAB" "$tab")
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
$lw
$(if ($isModel) { "A`n" })$png
N
Y
"@ $result
    Write-Host ("Plot " + $tab + ": " + (Test-Path $png))
    if (-not (Test-Path $png)) { $plotLog -split "`n" | Select-Object -Last 15 }
}
