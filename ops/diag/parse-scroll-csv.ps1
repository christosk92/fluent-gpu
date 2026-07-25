<#
.SYNOPSIS
  Summarise a ScrollTrace CSV — including captures taken BEFORE any of this tooling existed.

.DESCRIPTION
  The first task of a scroll-feel investigation is not to write instrumentation, it is to read what is already
  on disk. ops/scratch/hitch-measure-*.scroll.csv is a real diagnostics-compiled capture with thousands of
  offsetWrite rows, and it answers several of the questions the campaign asks without a single line of new code.

  This script is deliberately tolerant of BOTH schema generations:
    old: tMs,frame,kind,i0,i1,i2,f0..f5,auxMs
    new: tMs,frame,kind,i0,i1,i2,f0..f5,auxMs,state       (+ the `latency` record kind)

  Three traps it exists to avoid, each of which has produced a wrong conclusion before:

    * The frameTiming i2 column is UNCLAMPED frame dt. An idle gap of a minute appears there as a 59-second
      "frame". Any percentile taken over that column without a scroll-active gate fabricates a catastrophic
      stall. Rows above -IdleGapMs are reported SEPARATELY as idle gaps, never mixed into hitch statistics.

    * `frame` is not a join key. It counts Paint phase 7 only (RunFrame early-outs never reach it), is written
      without synchronisation, and suppresses no-input micro-frames. Join on tMs.

    * A missing signal is not a zero. A capture with no `phase`/`latch` rows is WHEEL-ONLY — it says nothing
      about the touchpad path, and reporting "0 touchpad problems" would be a lie of omission.

.EXAMPLE
  ops\diag\parse-scroll-csv.cmd ops\scratch\hitch-measure-20260723-191423.scroll.csv
  powershell -File ops\diag\parse-scroll-csv.ps1 -Csv <path> -Console <path> -Json out.json
#>
#requires -Version 5.1
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true, Position = 0)]
  [string]$Csv,
  [string]$Console,
  [string]$Json,
  [double]$IdleGapMs = 250.0
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Csv)) { throw "No such CSV: $Csv" }
function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Note($m) { Write-Host "    $m" -ForegroundColor DarkGray }

# PowerShell 5.1 renders single-element arrays as scalars in JSON; force arrays at the boundary.
function AsArray($x) { if ($null -eq $x) { return @() } return @($x) }

function Stats([double[]]$v) {
  if ($v.Count -eq 0) { return $null }
  $s = $v | Sort-Object
  # p50/p95/max only. NOT p99/p99.9: a ten-second phase is a few hundred frames, and a stable 1% low needs
  # roughly a thousand. Reporting a percentile the sample cannot support is how noise becomes a finding.
  $p = { param($q) $s[[math]::Min($s.Count - 1, [int][math]::Floor($q * ($s.Count - 1)))] }
  return [ordered]@{
    count = $v.Count
    mean  = [math]::Round((($v | Measure-Object -Average).Average), 3)
    p50   = [math]::Round((& $p 0.50), 3)
    p95   = [math]::Round((& $p 0.95), 3)
    max   = [math]::Round((($v | Measure-Object -Maximum).Maximum), 3)
  }
}

Step "Reading $Csv"
$rows = Import-Csv $Csv
if ($rows.Count -eq 0) { throw "CSV has no data rows: $Csv" }
$cols = $rows[0].PSObject.Properties.Name
$hasState = $cols -contains 'state'
Note ("schema: " + $(if ($hasState) { "current (state column present)" } else { "legacy (no state column) — phase slicing unavailable" }))

# ── kind histogram ───────────────────────────────────────────────────────────────────────────────────────────
$byKind = @{}
foreach ($r in $rows) {
  $k = $r.kind
  if ($byKind.ContainsKey($k)) { $byKind[$k] += 1 } else { $byKind[$k] = 1 }
}
Write-Host ""
Step "Record-kind histogram ($($rows.Count) rows)"
foreach ($k in ($byKind.Keys | Sort-Object { -$byKind[$_] })) {
  Write-Host ("    {0,-14} {1,8}" -f $k, $byKind[$k])
}

# ── coverage: which INPUT PATHS does this capture actually contain? ──────────────────────────────────────────
$contactKinds = @('phase', 'latch', 'velSample', 'release', 'gestureEnd', 'coalesce')
$contactRows = 0
foreach ($k in $contactKinds) { if ($byKind.ContainsKey($k)) { $contactRows += $byKind[$k] } }
$wheelRows = 0
foreach ($k in @('rawWheel', 'wheelSeed', 'wheelCancel')) { if ($byKind.ContainsKey($k)) { $wheelRows += $byKind[$k] } }
$coverage = 'both'
if ($contactRows -eq 0 -and $wheelRows -gt 0) { $coverage = 'wheel-only' }
elseif ($wheelRows -eq 0 -and $contactRows -gt 0) { $coverage = 'contact-only' }
elseif ($wheelRows -eq 0 -and $contactRows -eq 0) { $coverage = 'none' }

Write-Host ""
Step "Input-path coverage: $coverage"
if ($coverage -eq 'wheel-only') {
  Write-Host "    WHEEL-ONLY: zero phase/latch/velSample rows. This capture contains no touchpad or touch" -ForegroundColor Yellow
  Write-Host "    contact gesture at all, so it cannot support ANY conclusion about contact tracking, gluedness" -ForegroundColor Yellow
  Write-Host "    or the resampler. That is a gap in the capture, not a clean result." -ForegroundColor Yellow
}

# ── frames vs hitches ────────────────────────────────────────────────────────────────────────────────────────
$frameRows = @($rows | Where-Object { $_.kind -eq 'frame' })
$ftRows = @($rows | Where-Object { $_.kind -eq 'frameTiming' })
$hitchPct = 0.0
if ($frameRows.Count -gt 0) { $hitchPct = [math]::Round(100.0 * $ftRows.Count / $frameRows.Count, 2) }

Write-Host ""
Step "Frames"
Note "frame rows: $($frameRows.Count)   frameTiming (dt > 12ms) rows: $($ftRows.Count)   => $hitchPct% of recorded frames exceeded 12 ms"

$phaseStats = $null
$idleGaps = @()
if ($ftRows.Count -gt 0) {
  # f0..f5 = flush / layout / anim / record / submit / fenceWait, per the FrameTiming emit contract.
  $phaseStats = [ordered]@{
    flushMs     = Stats(@($ftRows | ForEach-Object { (Num $_.f0) }))
    layoutMs    = Stats(@($ftRows | ForEach-Object { (Num $_.f1) }))
    animMs      = Stats(@($ftRows | ForEach-Object { (Num $_.f2) }))
    recordMs    = Stats(@($ftRows | ForEach-Object { (Num $_.f3) }))
    submitMs    = Stats(@($ftRows | ForEach-Object { (Num $_.f4) }))
    fenceWaitMs = Stats(@($ftRows | ForEach-Object { (Num $_.f5) }))
  }
  Write-Host ""
  Step "Per-phase cost on hitch frames (ms)"
  foreach ($k in $phaseStats.Keys) {
    $s = $phaseStats[$k]
    Write-Host ("    {0,-12} mean {1,7}  p50 {2,7}  p95 {3,7}  max {4,8}" -f $k, $s.mean, $s.p50, $s.p95, $s.max)
  }
  $sub = $phaseStats.submitMs.mean; $fen = $phaseStats.fenceWaitMs.mean
  if ($sub -gt 0) {
    $share = [math]::Round(100.0 * $fen / $sub, 1)
    Note "fenceWait is $share% of submit — submit time is BLOCKED time, not command-build work, when this is high."
  }

  # i2 = UNCLAMPED dt x100. Split idle gaps out instead of letting them poison the hitch distribution.
  $dt = @($ftRows | ForEach-Object { (Num $_.i2) / 100.0 })
  $idleGaps = @($dt | Where-Object { $_ -gt $IdleGapMs })
  $realHitches = @($dt | Where-Object { $_ -le $IdleGapMs })
  Write-Host ""
  Step "Raw frame dt (i2, unclamped)"
  if ($realHitches.Count -gt 0) {
    $s = Stats($realHitches)
    Write-Host ("    hitches (<= $IdleGapMs ms): count {0}  p50 {1}  p95 {2}  max {3}" -f $s.count, $s.p50, $s.p95, $s.max)
  }
  if ($idleGaps.Count -gt 0) {
    $mx = [math]::Round((($idleGaps | Measure-Object -Maximum).Maximum), 1)
    Write-Host "    idle gaps (> $IdleGapMs ms): $($idleGaps.Count), largest ${mx} ms — EXCLUDED from hitch stats." -ForegroundColor Yellow
    Write-Host "    These are the loop legitimately not running (no work). Counting them as stalls is the single" -ForegroundColor Yellow
    Write-Host "    most common way this column produces a fabricated catastrophic result." -ForegroundColor Yellow
  }
}

# ── note 113: "the loop was not running" vs "the work was slow" ──────────────────────────────────────────────
$note113 = @($rows | Where-Object { $_.kind -eq 'note' -and $_.i0 -eq '113' })
$slackShare = $null
if ($ftRows.Count -gt 0) {
  $slackShare = [math]::Round(100.0 * $note113.Count / $ftRows.Count, 1)
  Write-Host ""
  Step "Hitch attribution"
  Note "note-113 rows (slack > 12 ms): $($note113.Count) of $($ftRows.Count) hitches = $slackShare%"
  Note "Those are frames where raw dt far exceeded the measured work: the loop WAS NOT RUNNING. Nothing was"
  Note "slow on them, so optimising a render phase cannot fix that share of the hitches."
}

# ── layout churn during scroll ───────────────────────────────────────────────────────────────────────────────
$note100 = @($rows | Where-Object { $_.kind -eq 'note' -and $_.i0 -eq '100' }).Count
$note101 = @($rows | Where-Object { $_.kind -eq 'note' -and $_.i0 -eq '101' }).Count
$note111 = @($rows | Where-Object { $_.kind -eq 'note' -and $_.i0 -eq '111' }).Count
Write-Host ""
Step "Scroll-path notes"
Note "100 anchor re-pin: $note100    101 resampler no-extrapolation clamp: $note101    111 per-row extent correction: $note111"
if ($note100 -gt 0) { Note "An anchor re-pin MOVES THE FRAME OF REFERENCE — tracking-lag samples spanning one are not comparable." }

# ── latency rows (only present in captures from the instrumented build) ──────────────────────────────────────
$latRows = @($rows | Where-Object { $_.kind -eq 'latency' })
$latency = $null
if ($latRows.Count -gt 0) {
  Write-Host ""
  Step "Latency rows: $($latRows.Count)"
  $qualities = @{ '0' = 'tick'; '1' = 'dequeue'; '2' = 'receive'; '3' = 'hardware' }
  $qHist = @{}
  foreach ($r in $latRows) {
    $q = [int]$r.i1 -band 0xFF
    $name = $qualities["$q"]; if (-not $name) { $name = "?$q" }
    if ($qHist.ContainsKey($name)) { $qHist[$name] += 1 } else { $qHist[$name] = 1 }
  }
  foreach ($k in $qHist.Keys) { Note "genStampQuality=${k}: $($qHist[$k])" }
  if (-not $qHist.ContainsKey('hardware')) {
    Write-Host "    No hardware-grade input stamps: sub-frame latency percentiles are NOT publishable from this" -ForegroundColor Yellow
    Write-Host "    capture. Report them as insufficientData, not as a measured value." -ForegroundColor Yellow
  }
  $latency = [ordered]@{
    rows                = $latRows.Count
    stampQuality        = $qHist
    lagDip              = Stats(@($latRows | ForEach-Object { (Num $_.f0) }))
    wakeOverheadMs      = Stats(@($latRows | ForEach-Object { (Num $_.f1) }))
    frameOverrunMs      = Stats(@($latRows | ForEach-Object { (Num $_.f2) }))
    clockSampleSkewMs   = Stats(@($latRows | ForEach-Object { (Num $_.f3) }))
    presentIntervalMs   = Stats(@($latRows | Where-Object { (Num $_.f4) -gt 0 } | ForEach-Object { (Num $_.f4) }))
    # i2 packs two counts: low 16 = stamp-derived missed slots, high 16 = OS-attested biased by +1 (0 = not attested).
    missedVsyncsSum     = (($latRows | ForEach-Object { [int]$_.i2 -band 0xFFFF } | Measure-Object -Sum).Sum)
    missedVsyncsAttestedSum = $(
      $a = @($latRows | ForEach-Object { ([int]$_.i2 -shr 16) -band 0xFFFF } | Where-Object { $_ -gt 0 })
      if ($a.Count -gt 0) { (($a | ForEach-Object { $_ - 1 } | Measure-Object -Sum).Sum) } else { $null })
    neverPresentedRows  = @($latRows | Where-Object { [int]$_.i0 -eq 0 }).Count
  }
  Note "missedVsyncs total: $($latency.missedVsyncsSum)   neverPresented (skip-submit) samples: $($latency.neverPresentedRows)"
}
else {
  Write-Host ""
  Note "No 'latency' rows: this capture predates the input-to-present correlation sensor, or scroll was never"
  Note "active while the trace was armed. Pillar-A questions cannot be answered from it."
}

# ── console.txt cross-read (optional) ────────────────────────────────────────────────────────────────────────
# NOTE the name: PowerShell variables are CASE-INSENSITIVE, so a local `$console` would silently clobber the
# `$Console` parameter and the whole block below would never run (it did, once — and the summary quietly lost its
# console section with no error at all).
$consoleSummary = $null
if ($Console) {
  if (-not (Test-Path $Console)) { throw "No such console log: $Console" }
  $lines = Get-Content $Console
  $anchor = @($lines | Where-Object { $_ -match '^\[scrolltrace\] anchor ' })
  $fps = @($lines | Where-Object { $_ -match '^\[fps\]' })
  $consoleSummary = [ordered]@{
    path          = $Console
    lines         = $lines.Count
    anchorPresent = ($anchor.Count -gt 0)
    fpsLines      = $fps.Count
    spikeLines    = @($fps | Where-Object { $_ -match 'SPIKE' }).Count
    offsetJumps   = @($lines | Where-Object { $_ -match '^\[OFFSET-JUMP\]' }).Count
  }
  Write-Host ""
  Step "Console log"
  Note "lines $($consoleSummary.lines)   [fps] $($consoleSummary.fpsLines) (SPIKE $($consoleSummary.spikeLines))   [OFFSET-JUMP] $($consoleSummary.offsetJumps)"
  if (-not $consoleSummary.anchorPresent) {
    Write-Host "    NO ANCHOR LINE. The console and the CSV therefore share no time axis and cannot be" -ForegroundColor Yellow
    Write-Host "    correlated. Common cause: the launcher redirected with '2>' instead of '*>&1 | Tee-Object'," -ForegroundColor Yellow
    Write-Host "    which drops stdout, or the build predates the anchor." -ForegroundColor Yellow
  }
}

# ── machine-readable output ──────────────────────────────────────────────────────────────────────────────────
if ($Json) {
  $out = [ordered]@{
    schemaVersion   = 1
    generatedAtUtc  = (Get-Date).ToUniversalTime().ToString('o')
    source          = (Resolve-Path $Csv).Path
    schema          = $(if ($hasState) { 'state' } else { 'legacy' })
    rows            = $rows.Count
    kindHistogram   = $byKind
    inputCoverage   = $coverage
    frames          = $frameRows.Count
    hitchFrames     = $ftRows.Count
    hitchPct        = $hitchPct
    phaseMsOnHitch  = $phaseStats
    note113Count    = $note113.Count
    note113PctOfHitches = $slackShare
    idleGapCount    = (AsArray $idleGaps).Count
    notes           = [ordered]@{ anchorRepin = $note100; resamplerClamp = $note101; extentCorrection = $note111 }
    latency         = $latency
    console         = $consoleSummary
  }
  $text = $out | ConvertTo-Json -Depth 12
  # BOM-free: a UTF-8 BOM breaks naive downstream readers, and -Encoding utf8 writes one on 5.1.
  [System.IO.File]::WriteAllText($Json, $text, (New-Object System.Text.UTF8Encoding($false)))
  Write-Host ""
  Step "Wrote $Json"
}
