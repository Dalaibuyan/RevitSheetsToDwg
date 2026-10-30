# Run a TestGen dump command (DUMPLAYOUTS, DUMPDIMSTYLES, DUMPDIMS) on a DWG and print its output lines.
#   powershell -File tests\dump.ps1 -Dwg x.dwg [-Cmd DUMPLAYOUTS]
param([Parameter(Mandatory)][string]$Dwg, [string]$Cmd = 'DUMPLAYOUTS')
$tmp = Join-Path $env:TEMP ("dump_" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
$tg = "$PSScriptRoot\TestGen\bin\Release\TestGen.dll" -replace '\\', '/'
$env:DUMP_FILE = $Dwg
$scr = "(setvar `"SECURELOAD`" 0)`r`n(command `"_.NETLOAD`" `"$tg`")`r`n$Cmd`r`n"
[IO.File]::WriteAllText("$tmp\d.scr", $scr)
$p = Start-Process 'C:\Program Files\Autodesk\AutoCAD 2020\accoreconsole.exe' -ArgumentList "/s `"$tmp\d.scr`"" -NoNewWindow -PassThru -RedirectStandardOutput "$tmp\log.txt"
if (-not $p.WaitForExit(180000)) { $p.Kill() }
$text = [IO.File]::ReadAllText("$tmp\log.txt", [Text.Encoding]::Unicode)
$text -split "`r?`n" | Where-Object { $_ -match '^(DUMP|DIM)' }
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
