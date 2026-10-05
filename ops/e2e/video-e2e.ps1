<#
.SYNOPSIS
  One-command automated end-to-end video test with timings (FluentGpu gallery host, real D3D12 + DirectComposition + Media Foundation).

.DESCRIPTION
  Builds src\FluentGpu.WindowsApp in Release once (skip with -NoBuild), runs the gallery exe with `--video-e2e` into a
  timestamped folder under ops\e2e\out\, then prints the markdown results table and the exit verdict. The probe opens
  real windows (the gallery main window plus a detached pop-out) on the current desktop and closes them again.

  Exit code: 0 = every thresholded metric PASS, 2 = at least one FAIL, 1 = the run could not complete.
  See ops\e2e\README.md for what each scenario measures.

.PARAMETER NoBuild   Skip the Release build and run the exe that is already there.
.PARAMETER Seconds   Length of the pop-out steady-state window in scenario S2 (default 15).
.PARAMETER Switches  Number of source switches in scenario S4 (default 10).
.PARAMETER Cycles    Number of popup open / popup close / main-window resize rounds in scenario S3 (default 20).
.PARAMETER OutRoot   Parent folder for the timestamped run folder (default ops\e2e\out).

.EXAMPLE
  powershell -File ops\e2e\video-e2e.ps1
  powershell -File ops\e2e\video-e2e.ps1 -NoBuild -Seconds 30 -Switches 20
#>
#requires -Version 5.1
[CmdletBinding()]
param(
  [switch]$NoBuild,
  [double]$Seconds = 15,
  [int]$Switches = 10,
  [int]$Cycles = 20,
  [string]$OutRoot
)
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$proj = Join-Path $repo 'src\FluentGpu.WindowsApp'
$exe  = Join-Path $proj 'bin\Release\net10.0\FluentGpu.WindowsApp.exe'
if (-not $OutRoot) { $OutRoot = Join-Path $PSScriptRoot 'out' }
$stamp  = Get-Date -Format 'yyyyMMdd-HHmmss'
$outDir = Join-Path $OutRoot $stamp
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

if (-not $NoBuild) {
  Write-Host "[video-e2e] building $proj (Release)..."
  & dotnet build $proj -c Release -m --nologo -v quiet -clp:ErrorsOnly
  if ($LASTEXITCODE -ne 0) { Write-Host "[video-e2e] build failed (exit $LASTEXITCODE)"; exit 1 }
}
if (-not (Test-Path $exe)) { Write-Host "[video-e2e] exe not found: $exe (build first, or drop -NoBuild)"; exit 1 }

Write-Host "[video-e2e] running; output folder: $outDir"
Write-Host "[video-e2e] this opens real windows for about 90 seconds; do not minimise the gallery window."
$stderr = Join-Path $outDir 'stderr.txt'
$stdout = Join-Path $outDir 'stdout.txt'
$argList = @('--video-e2e', ('"' + $outDir + '"'), '--seconds', $Seconds.ToString([Globalization.CultureInfo]::InvariantCulture), '--switches', $Switches, '--cycles', $Cycles)
$p = Start-Process -FilePath $exe -ArgumentList $argList -WorkingDirectory (Split-Path $exe) -PassThru -Wait -NoNewWindow `
       -RedirectStandardError $stderr -RedirectStandardOutput $stdout
$code = $p.ExitCode

$md = Join-Path $outDir 'video-e2e.md'
if (Test-Path $md) {
  Write-Host ''
  Get-Content -LiteralPath $md | ForEach-Object { Write-Host $_ }
} else {
  Write-Host "[video-e2e] no report was written (the probe could not start). Last stderr lines:"
  if (Test-Path $stderr) { Get-Content -LiteralPath $stderr -Tail 20 | ForEach-Object { Write-Host $_ } }
  if ($code -eq 0) { $code = 1 }
}

$verdict = switch ($code) { 0 { 'PASS' } 2 { 'FAIL (at least one metric over its threshold)' } default { 'INCOMPLETE (the run could not finish)' } }
Write-Host ''
Write-Host "[video-e2e] verdict: $verdict (exit $code)"
Write-Host "[video-e2e] report: $outDir"
exit $code
