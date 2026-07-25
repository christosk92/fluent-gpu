<#
.SYNOPSIS
  GATE: prove PresentMon can see Wavee's swapchain at all before any capture session is designed around it.

.DESCRIPTION
  PresentMon has two documented blind spots that would silently invalidate every present-side number:

    1. Hardware_Direct_Flip is not uniquely detectable and is REPORTED AS Composed_Flip. Tolerable — the two
       differ by roughly one refresh of latency, and the mode is recorded per phase so it can be bucketed.

    2. The DirectComposition composition-ATLAS path (IDCompositionSurface / BeginDraw, as opposed to a
       composition swapchain) is documented as producing "incorrect/misleading metrics", because the tool cannot
       track composition dependencies. Wavee runs composited (Mica), so this is a real possibility and NOT
       something to assume away. If Wavee lands there, the entire external-measurement strategy is void and the
       in-app DXGI/DWM probes (already built: AppHost.LastPresentStats) become the sole present-side truth.

  This script answers (2) empirically. It is a gate, not a formality: run it once per machine before trusting
  any presentmon.csv, and record the result in the session manifest.

.EXAMPLE
  ops\diag\probe-presentmode.cmd
  powershell -File ops\diag\probe-presentmode.ps1 -Seconds 15 -ProcessName Wavee.exe

.NOTES
  Windows PowerShell 5.1 compatible (no &&, no ternary, no ??). Wavee must ALREADY BE RUNNING and scrolling —
  an idle app presents rarely and yields a uselessly small sample.
#>
#requires -Version 5.1
[CmdletBinding()]
param(
  [string]$ProcessName = 'Wavee.exe',
  [int]$Seconds = 10,
  [string]$OutFile
)
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Warn($m) { Write-Host "    $m" -ForegroundColor Yellow }

# ── locate PresentMon ────────────────────────────────────────────────────────────────────────────────────────
$pm = $null
$candidates = @(
  (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\presentmon.exe'),
  'presentmon.exe'
)
foreach ($c in $candidates) {
  $r = Get-Command $c -ErrorAction SilentlyContinue
  if ($r) { $pm = $r.Source; break }
}
if (-not $pm) {
  throw @"
PresentMon not found. Install it (winget install Intel.PresentMon.Console) or skip this gate.

SKIPPING IS A SUPPORTED CHOICE: record presentMonAvailable=false in the session manifest and treat the in-app
DXGI/DWM present statistics as the sole present-side truth. What you lose is the INDEPENDENT witness — every
cadence number then comes from the same process being measured.
"@
}
Step "PresentMon: $pm"

# The installed binary may be x64 running under Prism emulation on an ARM64 machine — real CPU overhead during
# the very gesture being measured. Say so rather than silently paying it.
$osArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
if ($osArch -eq 'Arm64') {
  Warn "OS is ARM64. If this presentmon.exe is an x64 build it runs under Prism emulation and costs real CPU"
  Warn "during the capture. For a MEASURED arm prefer native xperf recording + offline 'presentmon --etl_file'."
}

# ── preflight: the target must be running, and we need ETW rights ────────────────────────────────────────────
$procBase = [System.IO.Path]::GetFileNameWithoutExtension($ProcessName)
$target = Get-Process -Name $procBase -ErrorAction SilentlyContinue
if (-not $target) { throw "$ProcessName is not running. Start it, navigate to a scrollable page, then re-run and SCROLL during the capture." }

$id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object System.Security.Principal.WindowsPrincipal($id)
$elevated = $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $elevated) { Warn "Not elevated — passing --restart_as_admin (expect one UAC prompt)." }

if (-not $OutFile) {
  $dir = Join-Path $PSScriptRoot 'sessions'
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $OutFile = Join-Path $dir ('pm-probe-' + (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss') + '.csv')
}
if (Test-Path $OutFile) { Remove-Item $OutFile -Force }

# ── capture ──────────────────────────────────────────────────────────────────────────────────────────────────
# Pass NEITHER --v1_metrics NOR --v2_metrics: the default header is the superset, and --v2_metrics DROPS
# MsBetweenPresents / MsBetweenDisplayChange / MsInPresentAPI / MsUntilDisplayed — precisely the columns that
# distinguish submit cadence from photon cadence. --write_display_metadata is rejected by 2.5.1; do not add it.
$pmArgs = @(
  '--process_name', $ProcessName,
  '--timed', "$Seconds", '--terminate_after_timed',
  '--qpc_time', '--write_display_time', '--write_frame_id', '--track_etw_status',
  '--no_console_stats', '--output_file', $OutFile
)
if (-not $elevated) { $pmArgs += '--restart_as_admin' }

Step "Capturing $Seconds s — SCROLL NOW (an idle window presents rarely and proves nothing)"
& $pm @pmArgs
if (-not (Test-Path $OutFile)) { throw "PresentMon produced no output file: $OutFile" }

# ── verdict ──────────────────────────────────────────────────────────────────────────────────────────────────
$rows = Import-Csv $OutFile
if ($rows.Count -eq 0) { throw "PresentMon captured 0 rows. Was the app presenting? Try scrolling continuously during the capture." }

$modeCol = $null
foreach ($n in @('PresentMode', 'PresentModeString')) {
  if ($rows[0].PSObject.Properties.Name -contains $n) { $modeCol = $n; break }
}
if (-not $modeCol) { throw "No PresentMode column in $OutFile — the header is unexpected; inspect the file by hand." }

Write-Host ""
Step "PresentMode histogram over $($rows.Count) presents"
$hist = $rows | Group-Object -Property $modeCol | Sort-Object Count -Descending
foreach ($g in $hist) {
  $pct = [math]::Round(100.0 * $g.Count / $rows.Count, 1)
  Write-Host ("    {0,-40} {1,7}  {2,5}%" -f $g.Name, $g.Count, $pct)
}

# ETW loss makes every derived number untrustworthy; surface it rather than averaging over holes.
foreach ($lossCol in @('EtwEventsLost', 'EtwBuffersLost', 'OverflowedPresents')) {
  if ($rows[0].PSObject.Properties.Name -contains $lossCol) {
    $lost = ($rows | Measure-Object -Property $lossCol -Maximum).Maximum
    if ($lost -gt 0) { Warn "$lossCol = $lost — this capture is NOT trustworthy; reduce load and re-run." }
  }
}

if ($hist.Count -gt 1) {
  Warn "PresentMode CHANGED mid-capture. That is not a regression — Windows promotes/demotes"
  Warn "Composed:Flip <-> Hardware Composed:Independent Flip on maximize, occlusion and MPO availability,"
  Warn "and the two differ by about one refresh of latency. Bucket every metric by mode."
}

# The atlas path is the one that voids the strategy. Composition SWAPCHAIN modes are fine.
$atlas = $hist | Where-Object { $_.Name -match 'Composed:\s*Composition\s*Atlas' }
Write-Host ""
if ($atlas) {
  Write-Host "GATE FAILED: Wavee is presenting through the DirectComposition composition-atlas path." -ForegroundColor Red
  Write-Host "PresentMon documents this as producing incorrect/misleading metrics (it cannot track composition" -ForegroundColor Red
  Write-Host "dependencies). Do NOT use presentmon.csv for present-side truth on this machine. Fall back to the" -ForegroundColor Red
  Write-Host "in-app DXGI GetFrameStatistics + DwmGetCompositionTimingInfo probe (AppHost.LastPresentStats), and" -ForegroundColor Red
  Write-Host "set presentMonAvailable=false in the session manifest." -ForegroundColor Red
  exit 2
}
Write-Host "GATE PASSED: no composition-atlas presents. PresentMon output is usable for present-side metrics." -ForegroundColor Green
Write-Host "  csv: $OutFile"
exit 0
