<#
.SYNOPSIS
  Turn a raw capture bundle into feel-summary.json + a copy of the diagnosis rubric (AGENT.md).

.DESCRIPTION
  Reads console.txt, scroll.csv, phases.jsonl, manifest.json and any presentmon.csv, and emits ONE machine-readable
  summary whose first block is validity: whether the capture can be trusted at all.

  Three rules this script enforces, all of which exist because breaking them produces a confident wrong answer:

    1. HARD-FAIL beats an empty summary. No anchor line, no [fps] lines, or a diag build with no latency rows means
       the instrument did not arm. An empty summary reads as "no problems found"; a hard failure reads as "measure
       again", which is the truth.

    2. Never write 0 for something that was not measured. Every unavailable field is null with a sibling
       reasonNotMeasured. A 0 in a ranked bucket list de-ranks a real cause to the bottom.

    3. p50/p95/max only. A ten-second phase is a few hundred frames; a stable 1% low needs about a thousand and a
       0.1% low about ten thousand. p99/p99.9/"1% low" are not computed here at any sample size, because their
       presence in a table invites comparison the protocol cannot support.

  Every bucket carries a PREDICATE and a REFUTER. A bucket with no way to be wrong is not a finding, it is a guess
  with a number attached, so any such bucket is dropped rather than ranked.

.EXAMPLE
  ops\diag\pack-feel-summary.cmd -Session ops\diag\sessions\20260725-120000-d082d67
#>
#requires -Version 5.1
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$Session,
  # The paired plain-Release arm of the SAME gesture script. Turns observer cost from an assumption into a number:
  # without it, no absolute millisecond figure from a diag bundle is interpretable, because some unknown share of
  # it is the instrument. Pass the control session directory.
  [string]$Control
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Session)) { throw "No such session directory: $Session" }
$Session = (Resolve-Path $Session).Path
function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Info($m) { Write-Host "    $m" -ForegroundColor DarkGray }
function Warn($m) { Write-Host "    $m" -ForegroundColor Yellow }

function WriteJsonNoBom($obj, $path) {
  $text = $obj | ConvertTo-Json -Depth 14
  [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}
function Stats([double[]]$v) {
  if (-not $v -or $v.Count -eq 0) { return $null }
  $s = $v | Sort-Object
  $q = { param($f) $s[[math]::Min($s.Count - 1, [int][math]::Floor($f * ($s.Count - 1)))] }
  return [ordered]@{
    count = $v.Count
    mean = [math]::Round((($v | Measure-Object -Average).Average), 3)
    p50 = [math]::Round((& $q 0.50), 3)
    p95 = [math]::Round((& $q 0.95), 3)
    max = [math]::Round((($v | Measure-Object -Maximum).Maximum), 3)
    min = [math]::Round((($v | Measure-Object -Minimum).Minimum), 3)
  }
}
function NotMeasured($reason) { return [ordered]@{ value = $null; reasonNotMeasured = $reason } }

# ScrollTrace writes an EMPTY field for an exact 0 (it is a size optimisation over millions of rows), and several
# of its columns are deliberately SIGNED — frameOverrun, clockSampleSkew, lagDip. So the field must be parsed, not
# string-prefixed: "0" + "-6.2" is not a number, and a naive prefix trick throws on exactly the negative values
# that carry the most diagnostic weight (headroom, and a clock sampled behind the frame).
function Num($s) {
  if ($null -eq $s -or "$s".Trim().Length -eq 0) { return 0.0 }
  $d = 0.0
  if ([double]::TryParse("$s", [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { return $d }
  return 0.0
}

$consolePath = Join-Path $Session 'console.txt'
$csvPath = Join-Path $Session 'scroll.csv'
$phasesPath = Join-Path $Session 'phases.jsonl'
$manifestPath = Join-Path $Session 'manifest.json'
$pmPath = Join-Path $Session 'presentmon.csv'

Step "Packing $Session"

$manifest = $null
if (Test-Path $manifestPath) { $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json }

# ── validity: computed FIRST, because everything downstream is meaningless without it ────────────────────────
$untrusted = @()
$hardFail = @()

$consoleLines = @()
if (Test-Path $consolePath) { $consoleLines = Get-Content $consolePath }
else { $hardFail += 'console.txt missing' }

$anchorLine = @($consoleLines | Where-Object { $_ -match '^\[scrolltrace\] anchor ' }) | Select-Object -First 1
if (-not $anchorLine) {
  # No shared clock means no correlation between any two artifacts. Summarising anyway would produce numbers that
  # look joined and are not.
  $hardFail += 'no [scrolltrace] anchor line in console.txt - the artifacts share no time axis, so nothing can be correlated'
}
$anchorQpc = $null; $qpcFreq = $null; $anchorWall = $null; $traceArmed = $null
if ($anchorLine) {
  if ($anchorLine -match 'qpc=(\d+)') { $anchorQpc = [long]$Matches[1] }
  if ($anchorLine -match 'qpcFreq=(\d+)') { $qpcFreq = [long]$Matches[1] }
  if ($anchorLine -match 'wallUtc=(\S+)') { $anchorWall = $Matches[1] }
  if ($anchorLine -match 'trace=(\d)') { $traceArmed = ($Matches[1] -eq '1') }
}

$fpsLines = @($consoleLines | Where-Object { $_ -match '^\[fps\] ' })
if ($fpsLines.Count -eq 0) { $hardFail += 'zero [fps] lines - FG_FPS_LOG did not arm, and an empty summary would read as "no hitches"' }

$diagBuild = $false
if ($manifest -and $manifest.build) { $diagBuild = [bool]$manifest.build.fluentGpuDiag }

$csvRows = @()
if (Test-Path $csvPath) { $csvRows = Import-Csv $csvPath }
elseif ($diagBuild) { $hardFail += 'scroll.csv missing on a FLUENTGPU_DIAG build - the ring never armed or the process was killed before it flushed' }

$latRows = @($csvRows | Where-Object { $_.kind -eq 'latency' })
if ($diagBuild -and $csvRows.Count -gt 0 -and $latRows.Count -eq 0) {
  $hardFail += 'zero latency rows in scroll.csv - scroll was never active while the trace was armed, so pillar A has no data at all'
}

# The trailing idle phase exists to trigger the ring's idle flush. Without it the tail of the capture is whatever
# happened to be flushed by a full ring, which is not the same thing as "the session".
$trailingIdleFlush = $null
if ($csvRows.Count -gt 0) {
  $tail = @($csvRows | Select-Object -Last 60)
  $tailActive = @($tail | Where-Object { $_.kind -eq 'frame' -and $_.i1 -eq '1' }).Count
  $trailingIdleFlush = ($tailActive -eq 0)
  if (-not $trailingIdleFlush) { $untrusted += 'capture ends while scroll was still active - the trailing idle flush did not run, so the tail is truncated' }
}

# A bisection arm makes this bundle a TREATMENT. It is comparable ONLY to a control captured with otherwise
# identical switches, and it must never be read as a description of how the app behaves.
$bisectArm = $null
if ($manifest -and $manifest.switches -and $manifest.switches.noImagePump) { $bisectArm = 'noImagePump' }
if ($manifest -and $manifest.effectiveKnobs -and $manifest.effectiveKnobs.opaqueWindow) {
  if ($bisectArm) { $bisectArm = "$bisectArm+opaqueWindow" } else { $bisectArm = 'opaqueWindow' }
}
if ($bisectArm) { $untrusted += "this is a TREATMENT arm ($bisectArm), not an observation - compare it only against a control bundle captured with otherwise identical switches" }

$idleCpu = $null
if ($manifest -and $manifest.power) { $idleCpu = $manifest.power.idleCpuPctPreCapture }
if ($null -ne $idleCpu -and $idleCpu -gt 5.0) { $untrusted += "idle CPU was $idleCpu% before the capture - hitches may belong to another process" }
if ($manifest -and $manifest.build -and $manifest.build.gitDirty) { $untrusted += 'working tree was dirty - the capture is not reproducible from the git sha alone' }

# ── phases + subjective scores ───────────────────────────────────────────────────────────────────────────────
$phaseRecords = @()
if (Test-Path $phasesPath) {
  foreach ($line in (Get-Content $phasesPath)) {
    if ($line.Trim().Length -eq 0) { continue }
    $phaseRecords += ($line | ConvertFrom-Json)
  }
}

# ── console-derived cadence, per [fps] line ──────────────────────────────────────────────────────────────────
# Deltas of the monotonic counters, not levels. presentD/pubD/coal/skipD are printed by the host precisely so this
# does not have to be reconstructed from a trailing-window fps figure (which is forced to 0 whenever no present is
# observed inside its window - the mechanical source of the historical "present 0fps" ghost).
$scrollFps = @()
foreach ($l in $fpsLines) {
  $rec = [ordered]@{}
  if ($l -match 'tMs=([0-9.]+)') { $rec.tMs = [double]$Matches[1] }
  $rec.scroll = ($l -match '\bscroll\b')
  $rec.spike = ($l -match '\bSPIKE\b')
  if ($l -match 'loop (\d+)fps ([0-9.]+)ms') { $rec.loopFps = [int]$Matches[1]; $rec.frameMs = [double]$Matches[2] }
  if ($l -match 'presentD=(\d+)') { $rec.presentDelta = [int]$Matches[1] }
  if ($l -match 'pubD=(\d+)') { $rec.publishDelta = [int]$Matches[1] }
  if ($l -match 'coal=(\d+)') { $rec.coalesced = [int]$Matches[1] }
  if ($l -match 'lag=(\d+)') { $rec.renderLagFrames = [int]$Matches[1] }
  if ($l -match 'skipD=(\d+)') { $rec.skipDelta = [int]$Matches[1] }
  if ($l -match 'gpu ([0-9.]+)ms') { $rec.fenceWaitMs = [double]$Matches[1] }
  if ($l -match 'wait ([a-z-]+)(-?\d+)') { $rec.waitKind = $Matches[1]; $rec.waitMs = [int]$Matches[2] }
  if ($l -match '@(\d+)Hz') { $rec.panelHz = [int]$Matches[1] }
  if ($rec.Contains('tMs')) { $scrollFps += $rec }
}
$scrollOnly = @($scrollFps | Where-Object { $_.scroll })

# ── observer cost: this bundle's frame time vs the paired plain-Release arm ───────────────────────────────────
# Compared on the MEDIAN, not the mean: frame-time distributions have a long right tail, and one 500 ms outlier in
# either arm would swamp a mean and invent (or hide) an observer effect that is not there.
# Prefer SCROLL-ACTIVE lines: observer cost is only meaningful over the workload being measured. An idle window
# does almost no work per frame, so comparing idle medians measures the printing resolution, not the instrument.
function MedianFrameMs($lines) {
  $active = @($lines | Where-Object { $_ -match '\bscroll\b' })
  $use = $(if ($active.Count -ge 20) { $active } else { $lines })
  $v = @()
  foreach ($l in $use) { if ($l -match 'loop \d+fps ([0-9.]+)ms') { $v += [double]$Matches[1] } }
  if ($v.Count -eq 0) { return $null }
  $s = $v | Sort-Object
  return [math]::Round($s[[int][math]::Floor(0.5 * ($s.Count - 1))], 3)
}
# The [fps] line prints frame time to ONE decimal, so a sub-millisecond median is one or two quantization buckets
# and a ratio taken across it is noise amplified into a percentage. Below this, refuse.
$ObserverFloorMs = 1.0
$observerDelta = NotMeasured 'no paired plain-Release arm supplied - re-run the same gesture script without -Diag and pass -Control <that session>'
$controlInfo = $null
if ($Control) {
  if (-not (Test-Path $Control)) { throw "No such control session: $Control" }
  $Control = (Resolve-Path $Control).Path
  $ctlConsole = Join-Path $Control 'console.txt'
  if (-not (Test-Path $ctlConsole)) { throw "Control session has no console.txt: $Control" }
  $ctlManifest = $null
  if (Test-Path (Join-Path $Control 'manifest.json')) { $ctlManifest = Get-Content (Join-Path $Control 'manifest.json') -Raw | ConvertFrom-Json }
  $ctlDiag = $false
  if ($ctlManifest -and $ctlManifest.build) { $ctlDiag = [bool]$ctlManifest.build.fluentGpuDiag }
  if ($ctlDiag -eq $diagBuild) {
    # Comparing two bundles of the same flavour measures run-to-run noise, not observer cost, and would report a
    # near-zero delta that reads as "the instrument is free".
    $untrusted += "control bundle has the SAME build flavour as this one (fluentGpuDiag=$ctlDiag) - that measures run-to-run noise, not observer cost"
    $observerDelta = NotMeasured "control has the same build flavour (fluentGpuDiag=$ctlDiag); a paired arm must differ in exactly that one variable"
  }
  else {
    $ctlLines = @(Get-Content $ctlConsole | Where-Object { $_ -match '^\[fps\] ' })
    $ctlMed = MedianFrameMs $ctlLines
    $selfMed = MedianFrameMs $fpsLines
    if ($null -eq $ctlMed -or $null -eq $selfMed -or $ctlMed -le 0) {
      $observerDelta = NotMeasured 'one arm produced no parseable frame times'
    }
    elseif ($ctlMed -lt $ObserverFloorMs -or $selfMed -lt $ObserverFloorMs) {
      # Refuse rather than publish a large, meaningless percentage. Seen in practice: an idle unattended pair gave
      # medians of 0.3 ms and 0.1 ms, which divides out to -67% and looks like a spectacular result while being
      # entirely an artifact of the one-decimal print format.
      $observerDelta = NotMeasured "frame-time medians ($selfMed ms vs $ctlMed ms) are at or below the $ObserverFloorMs ms printing resolution - observer cost cannot be measured from a capture with no real workload. Re-run both arms with actual gestures."
    }
    else {
      $observerDelta = [math]::Round(100.0 * ($selfMed - $ctlMed) / $ctlMed, 1)
    }
    $controlInfo = [ordered]@{
      session = (Split-Path $Control -Leaf)
      buildFlavor = $(if ($ctlDiag) { 'FLUENTGPU_DIAG' } else { 'plain-Release' })
      fpsLineCount = $ctlLines.Count
      medianFrameMs = $ctlMed
      thisMedianFrameMs = $selfMed
    }
  }
}

# ── latency rows -> per-phase metrics ────────────────────────────────────────────────────────────────────────
# state word layout: phase bits 0-3, gesture 4-5, coldPass 6, repetition 7-10, abVariant 11-12.
function StatePhase($s) { if ($null -eq $s -or $s -eq '') { return 0 } return ([int]$s -band 0xF) }
function StateGesture($s) { if ($null -eq $s -or $s -eq '') { return 0 } return (([int]$s -shr 4) -band 0x3) }
function StateCold($s) { if ($null -eq $s -or $s -eq '') { return 0 } return (([int]$s -shr 6) -band 0x1) }
function StateRep($s) { if ($null -eq $s -or $s -eq '') { return 0 } return (([int]$s -shr 7) -band 0xF) }

$hasState = $false
if ($csvRows.Count -gt 0) { $hasState = ($csvRows[0].PSObject.Properties.Name -contains 'state') }

$gestureNames = @('idle', 'drag', 'inertia', 'settle')
$qualityNames = @('tick', 'dequeue', 'receive', 'hardware')
$stageNames = @('wakeOverhead', 'flush', 'layout', 'anim', 'record', 'imagePump', 'realizeCatchup', 'submit', 'fenceWait')

$phaseSummaries = @()
$phaseOrdinals = @()
if ($hasState -and $latRows.Count -gt 0) { $phaseOrdinals = @($latRows | ForEach-Object { StatePhase $_.state } | Sort-Object -Unique | Where-Object { $_ -gt 0 }) }

$stageTagTotals = @{}
foreach ($n in $stageNames) { $stageTagTotals[$n] = 0 }
$overrunFrames = 0

foreach ($ord in $phaseOrdinals) {
  $mine = @($latRows | Where-Object { (StatePhase $_.state) -eq $ord })
  # Repetition 1 is the WARM-UP and is excluded from every statistic. Pooling a cold pass (span record, glyph
  # raster, image decode, PSO warm) with warm ones lets the cold pass dominate the verdict.
  $warm = @($mine | Where-Object { (StateCold $_.state) -eq 0 })
  $name = "phase$ord"
  $rec = @($phaseRecords | Where-Object { $_.ord -eq $ord }) | Select-Object -First 1
  if ($rec) { $name = $rec.name }

  $insufficient = $false; $insufficientReason = $null
  if ($warm.Count -lt 20) {
    # Below this a percentile is a description of noise. Say so instead of printing one.
    $insufficient = $true
    $insufficientReason = "only $($warm.Count) warm scroll-active frames (need >= 20 before a percentile means anything)"
  }

  $qualities = @{}
  foreach ($r in $warm) {
    $qi = [int]$r.i1 -band 0xFF
    $qn = 'unknown'
    if ($qi -lt $qualityNames.Count) { $qn = $qualityNames[$qi] }
    if ($qualities.ContainsKey($qn)) { $qualities[$qn] += 1 } else { $qualities[$qn] = 1 }
  }
  $bestQuality = 'tick'
  foreach ($qn in @('hardware', 'receive', 'dequeue', 'tick')) { if ($qualities.ContainsKey($qn)) { $bestQuality = $qn; break } }

  $gestureHist = @{}
  foreach ($r in $warm) {
    $gn = $gestureNames[(StateGesture $r.state)]
    if ($gestureHist.ContainsKey($gn)) { $gestureHist[$gn] += 1 } else { $gestureHist[$gn] = 1 }
  }

  # Multi-label stage tags. Totals may exceed 100% of overrun frames on purpose: one frame legitimately carries
  # several, and collapsing to a single winner is how a secondary cause gets a fix it did not need.
  $stageHist = @{}
  $overrunHere = 0
  foreach ($r in $warm) {
    $mask = ([int]$r.i1 -shr 8)
    if ((Num $r.f2) -gt 0) { $overrunHere++ ; $overrunFrames++ }
    for ($b = 0; $b -lt $stageNames.Count; $b++) {
      if (($mask -band (1 -shl $b)) -ne 0) {
        $n = $stageNames[$b]
        if ($stageHist.ContainsKey($n)) { $stageHist[$n] += 1 } else { $stageHist[$n] = 1 }
        $stageTagTotals[$n] += 1
      }
    }
  }

  # i2 packs BOTH counts: low 16 = our stamp-derived missed slots, high 16 = the OS-attested count biased by +1
  # (0 = not attested). Where the attested count exists it SUPERSEDES ours - it is what the display pipeline did,
  # not what our post-Present timestamp implies - so they are reported side by side and never averaged together.
  $missed = @($warm | ForEach-Object { [int]$_.i2 -band 0xFFFF })
  $attestedRaw = @($warm | ForEach-Object { ([int]$_.i2 -shr 16) -band 0xFFFF } | Where-Object { $_ -gt 0 })
  $attestedSum = $null; $attestedMax = $null; $attestedFrames = $attestedRaw.Count
  if ($attestedFrames -gt 0) {
    $attestedSum = (($attestedRaw | ForEach-Object { $_ - 1 } | Measure-Object -Sum).Sum)
    $attestedMax = (($attestedRaw | ForEach-Object { $_ - 1 } | Measure-Object -Maximum).Maximum)
  }
  $neverPresented = @($warm | Where-Object { [int]$_.i0 -eq 0 }).Count
  $intervals = @($warm | Where-Object { (Num $_.f4) -gt 0 } | ForEach-Object { (Num $_.f4) })
  $intervalStats = Stats($intervals)
  # One scalar that balances throughput, outliers and consistency. Reported ALONGSIDE the distribution, never
  # instead of it: a single percentile cannot tell consistent 33ms frames from one 500ms frame among fast ones.
  $meanPlus2Sd = $null
  if ($intervals.Count -gt 2) {
    $m = ($intervals | Measure-Object -Average).Average
    $sd = [math]::Sqrt((($intervals | ForEach-Object { ($_ - $m) * ($_ - $m) } | Measure-Object -Sum).Sum) / ($intervals.Count - 1))
    $meanPlus2Sd = [math]::Round($m + 2 * $sd, 3)
  }

  # Average only phases a human actually scored. A null (unattended, skipped or out-of-range prompt) is EXCLUDED,
  # never coerced to 0 - a 0 is below the 1-5 scale and would drag the average toward "terrible" for a phase nobody
  # rated, turning an absence of evidence into evidence of a problem.
  $subj = @($phaseRecords | Where-Object { $_.ord -eq $ord -and -not $_.coldPass -and -not $_.synthetic })
  $gluedVals = @($subj | ForEach-Object { $_.gluedScore1to5 } | Where-Object { $null -ne $_ -and $_ -ge 1 -and $_ -le 5 })
  $steadyVals = @($subj | ForEach-Object { $_.steadyScore1to5 } | Where-Object { $null -ne $_ -and $_ -ge 1 -and $_ -le 5 })
  $gluedAvg = $null; $steadyAvg = $null
  if ($gluedVals.Count -gt 0) { $gluedAvg = [math]::Round((($gluedVals | Measure-Object -Average).Average), 2) }
  if ($steadyVals.Count -gt 0) { $steadyAvg = [math]::Round((($steadyVals | Measure-Object -Average).Average), 2) }

  $phaseSummaries += [ordered]@{
    ord = $ord; name = $name
    warmScrollActiveFrames = $warm.Count; coldFramesExcluded = ($mine.Count - $warm.Count)
    insufficientData = $insufficient; insufficientDataReason = $insufficientReason
    maxSupportedPercentile = 'p95'
    gestureStateHistogram = $gestureHist
    subjectiveGluedScore1to5 = $gluedAvg
    subjectiveSteadyScore1to5 = $steadyAvg
    latency = [ordered]@{
      genStampQuality = $bestQuality
      genStampQualityHistogram = $qualities
      # A sub-frame latency percentile off a `receive` stamp is a description of the producer's pump rate, not of
      # the input path. Refuse rather than publish it.
      inputToVblankOfPresentMs = $(if ($bestQuality -eq 'hardware') { $null } else { NotMeasured "genStampQuality=$bestQuality (below hardware)" })
      appliedVsIntendedDip = $(if ($insufficient) { NotMeasured $insufficientReason } else { Stats(@($warm | ForEach-Object { (Num $_.f0) })) })
      velocityDipPerMs = $(if ($insufficient) { NotMeasured $insufficientReason } else { Stats(@($warm | ForEach-Object { [math]::Abs((Num $_.f5)) })) })
      wakeOverheadMs = $(if ($insufficient) { NotMeasured $insufficientReason } else { Stats(@($warm | ForEach-Object { (Num $_.f1) })) })
      coalescingBiasNoted = $true
    }
    cadence = [ordered]@{
      presentIntervalMs = $intervalStats
      presentIntervalMsMeanPlus2Sd = $meanPlus2Sd
      missedVsyncsSum = (($missed | Measure-Object -Sum).Sum)
      missedVsyncsMax = (($missed | Measure-Object -Maximum).Maximum)
      frameOverrunMs = $(if ($insufficient) { NotMeasured $insufficientReason } else { Stats(@($warm | ForEach-Object { (Num $_.f2) })) })
      clockSampleSkewMs = $(if ($insufficient) { NotMeasured $insufficientReason } else { Stats(@($warm | ForEach-Object { (Num $_.f3) })) })
      overrunFrames = $overrunHere
      neverPresentedSamples = $neverPresented
      missedVsyncsAttestedSum = $(if ($attestedFrames -gt 0) { $attestedSum } else { NotMeasured 'no frame in this phase carried an attested vblank ordinal (DXGI frame statistics unavailable, disjoint, or the swapchain is not on a flip-model path)' })
      missedVsyncsAttestedMax = $(if ($attestedFrames -gt 0) { $attestedMax } else { NotMeasured 'see missedVsyncsAttestedSum' })
      attestedFrameCount = $attestedFrames
    }
    fanOut = [ordered]@{ stageTagsOverOneRefresh = $stageHist }
  }
}

if ($phaseOrdinals.Count -eq 0 -and $latRows.Count -gt 0) {
  Warn "Latency rows exist but carry no phase ordinals - the phase marker never reached the app (FG_SCROLL_PHASE_FILE)."
  $untrusted += 'latency rows carry no phase ordinal, so per-phase slicing is unavailable'
}

# ── buckets: predicate + refuter, ranked by measured tag frequency ───────────────────────────────────────────
$buckets = @()
function AddBucket($name, $predicate, $refuter, $tagged, $coverage, $verdict, $detail) {
  $script:buckets += [ordered]@{
    name = $name; predicate = $predicate; refuter = $refuter; refuterChecked = $true
    taggedFrames = $tagged; coveragePct = $coverage; verdict = $verdict; detail = $detail
  }
}

$totalWarm = 0
foreach ($p in $phaseSummaries) { $totalWarm += $p.warmScrollActiveFrames }

foreach ($n in $stageNames) {
  $tagged = $stageTagTotals[$n]
  $cov = 0.0
  if ($overrunFrames -gt 0) { $cov = [math]::Round(100.0 * $tagged / $overrunFrames, 1) }
  $verdict = 'refuted'
  if ($tagged -gt 0) { $verdict = 'likelyContributor' }
  if ($overrunFrames -eq 0) { $verdict = 'refuted' }
  if ($totalWarm -eq 0) { $verdict = 'insufficientData' }
  AddBucket "stage:$n" `
    "$n exceeded one refresh period on a frame that also missed its deadline" `
    "no overrun frame carried the $n tag, or the frame-overrun distribution shows headroom to spare (p95 < 0)" `
    $tagged $cov $verdict "tagged $tagged of $overrunFrames overrun frames"
}

$coalescedTotal = 0; $skipTotal = 0
foreach ($r in $scrollOnly) {
  if ($r.Contains('coalesced')) { $coalescedTotal = [math]::Max($coalescedTotal, $r.coalesced) }
  if ($r.Contains('skipDelta')) { $skipTotal += $r.skipDelta }
}
AddBucket 'dropOldestCoalesce' `
  'publishes exceeded presents while scroll was active - the render thread replaced frames it never showed' `
  'publishSequence minus presentedSequence stayed flat across the phase' `
  $coalescedTotal $null $(if ($coalescedTotal -gt 0) { 'likelyContributor' } else { 'refuted' }) `
  "peak publish-minus-present backlog $coalescedTotal"

AddBucket 'skipSubmitPacing' `
  'frames elided their submit while scroll was active (a ready frame was held, not slow work)' `
  'the FramesSkippedSubmit delta stayed 0 across the phase. NOTE: do NOT look for the pace-skip wait token - it is only assigned when the render thread is synchronous, so on the shipping async default its absence proves nothing' `
  $skipTotal $null $(if ($skipTotal -gt 0) { 'likelyContributor' } else { 'refuted' }) `
  "skipped submits during scroll-active lines: $skipTotal"

$offsetJumps = @($consoleLines | Where-Object { $_ -match '^\[OFFSET-JUMP\]' }).Count
$offsetJumpArmed = $true
if ($manifest -and $manifest.env -and $manifest.env.FG_OFFSET_JUMP) { $offsetJumpArmed = ($manifest.env.FG_OFFSET_JUMP.value -eq '1') }
AddBucket 'offsetDiscontinuity' `
  'at least one single offset write jumped further than the discontinuity threshold during a scroll phase' `
  'zero [OFFSET-JUMP] lines AND the flag was verifiably set to exactly "1" (any other value silently disables it, and the emptiness would then be a config artifact rather than evidence)' `
  $offsetJumps $null `
  $(if (-not $offsetJumpArmed) { 'notMeasured' } elseif ($offsetJumps -gt 0) { 'likelyContributor' } else { 'refuted' }) `
  "lines=$offsetJumps armed=$offsetJumpArmed"

$renderBudget = @($consoleLines | Where-Object { $_ -match '^\[renderbudget\]' }).Count
AddBucket 'reconcileFanOut' `
  'the every-frame re-render roster names components while scroll is active' `
  'the roster is empty. Do NOT use an empty [render-census] as the refutation - it is suppressed unless flush >= 12ms or comps >= 25, so a broad-but-cheap fan-out prints nothing at all' `
  $renderBudget $null `
  $(if (-not $diagBuild) { 'notMeasured' } elseif ($renderBudget -gt 0) { 'likelyContributor' } else { 'refuted' }) `
  $(if (-not $diagBuild) { 'RenderBudget is a no-op without FLUENTGPU_DIAG' } else { "[renderbudget] lines: $renderBudget" })

$scrollPerf = @($consoleLines | Where-Object { $_ -match '^\[scrollperf\]' })
$bindsMax = 0
foreach ($l in $scrollPerf) { if ($l -match 'bindsMax=(\d+)') { $bindsMax = [math]::Max($bindsMax, [int]$Matches[1]) } }
AddBucket 'scrollBindThrash' `
  'the per-frame scroll-bind evaluation count during scroll is above its idle baseline' `
  'bindsMax stayed at the idle baseline for the phase' `
  $bindsMax $null $(if ($scrollPerf.Count -eq 0) { 'insufficientData' } else { 'likelyContributor' }) `
  "peak binds/frame $bindsMax over $($scrollPerf.Count) [scrollperf] windows"

$realizeTag = $stageTagTotals['realizeCatchup'] + $stageTagTotals['imagePump']
AddBucket 'imageDecodeDuringScroll' `
  'the phase-7.5 image pump or the phase-7.6 realize catch-up exceeded one refresh period on a frame that missed its deadline' `
  'BISECTION ONLY: re-run the identical gesture script with -NoImagePump and show the same delayedFrames/missedVsyncs. Correlation is NOT enough here - the pump being busy during dropped presents does not make it the cause. Until that second bundle exists this bucket cannot be refuted, only suspected' `
  $realizeTag $null `
  $(if ($totalWarm -eq 0) { 'insufficientData' } elseif ($realizeTag -gt 0) { 'likelyContributor' } else { 'refuted' }) `
  $(if ($bisectArm -match 'noImagePump') { "THIS IS THE BISECTION ARM: tagged $realizeTag with the pump suppressed - compare against the control bundle" } else { "tagged $realizeTag of $overrunFrames overrun frames; run -NoImagePump to settle causation" })

$skewFlagged = 0
foreach ($p in $phaseSummaries) {
  $s = $p.cadence.clockSampleSkewMs
  if ($s -and $s.Contains('mean') -and [math]::Abs($s.mean) -gt 4.0) { $skewFlagged++ }
}
AddBucket 'clockSampling' `
  'the offset baked into a frame represents an instant materially different from when that frame was displayed (mean |clockSampleSkewMs| over ~half a refresh)' `
  'the mean skew sits within a packet interval of zero across every phase' `
  $skewFlagged $null `
  $(if ($phaseSummaries.Count -eq 0) { 'insufficientData' } elseif ($skewFlagged -gt 0) { 'likelyContributor' } else { 'refuted' }) `
  "$skewFlagged of $($phaseSummaries.Count) phases flagged"

# Rank by MEASURED tag frequency, not by a hardcoded order. Multi-label, so the total may exceed 100%.
$ranked = @($buckets | Where-Object { $_.verdict -eq 'likelyContributor' } | Sort-Object -Property taggedFrames -Descending | ForEach-Object { $_.name })
$noDominant = ($ranked.Count -eq 0)

# ── did the session even reproduce the complaint? ────────────────────────────────────────────────────────────
$reproduced = $null
# Synthetic phases are EXCLUDED from the reproduction question by construction: nobody touched the machine, so
# there is no observation to corroborate. Counting them would let an unattended instrument-validation run answer
# "did scrolling feel wrong", which it structurally cannot.
$syntheticPhases = @($phaseRecords | Where-Object { $_.synthetic }).Count
if ($syntheticPhases -gt 0) {
  $untrusted += "$syntheticPhases synthetic phase records (unattended run): no human performed the gestures or scored them, so NO glued/steady verdict may be drawn from this bundle - it validates the instrument only"
}
$scored = @($phaseRecords | Where-Object {
    -not $_.coldPass -and -not $_.synthetic -and
    (($null -ne $_.gluedScore1to5 -and $_.gluedScore1to5 -ge 1 -and $_.gluedScore1to5 -le 5) -or
     ($null -ne $_.steadyScore1to5 -and $_.steadyScore1to5 -ge 1 -and $_.steadyScore1to5 -le 5))
  })
if ($scored.Count -gt 0) {
  $bad = @($scored | Where-Object { ($_.gluedScore1to5 -ge 1 -and $_.gluedScore1to5 -le 3) -or ($_.steadyScore1to5 -ge 1 -and $_.steadyScore1to5 -le 3) })
  $reproduced = ($bad.Count -gt 0)
}

$trusted = ($hardFail.Count -eq 0)

$summary = [ordered]@{
  schemaVersion = 1
  generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
  generatorVersion = 1
  standardsBorrowed = @('present-cadence-vs-submit-cadence', 'multi-label-stage-attribution', 'signed-frame-overrun', 'animation-clock-skew', 'prediction-error-split')
  validity = [ordered]@{
    buildFlavor = $(if ($diagBuild) { 'FLUENTGPU_DIAG' } else { 'plain-Release' })
    fluentGpuDiag = $diagBuild
    traceArmed = $traceArmed
    idleCpuPctPreCapture = $idleCpu
    anchorPresent = ($null -ne $anchorLine)
    anchorWallUtc = $anchorWall
    anchorQpc = $anchorQpc
    qpcFrequency = $qpcFreq
    fpsLineCount = $fpsLines.Count
    scrollActiveFpsLineCount = $scrollOnly.Count
    csvRowCount = $csvRows.Count
    latencyRowCount = $latRows.Count
    trailingIdleFlushPresent = $trailingIdleFlush
    presentMonAvailable = (Test-Path $pmPath)
    bisectionArm = $bisectArm
    isObservation = ($null -eq $bisectArm)
    observerDeltaVsPlainReleasePct = $observerDelta
    observerControl = $controlInfo
    trusted = $trusted
    hardFailReasons = @($hardFail)
    untrustedReasons = @($untrusted)
  }
  environmentRef = './manifest.json'
  reproducedComplaint = $reproduced
  # false ⇒ instrument-validation only. The rubric forbids any glued/steady verdict from such a bundle.
  humanObserved = ($syntheticPhases -eq 0 -and $scored.Count -gt 0)
  syntheticPhaseCount = $syntheticPhases
  phases = @($phaseSummaries)
  buckets = @($buckets)
  globalVerdict = [ordered]@{
    rankedLikelyContributors = @($ranked)
    noDominantStage = $noDominant
    # Detection starts at the present side because that is where the symptom shows. FIXING starts upstream: a span
    # re-record storm or an image pump CAUSES the downstream present misses, so fixing the present symptom first
    # treats the wrong end.
    fixOrder = @('input/offset producers', 'reconcile + layout fan-out', 'record + image pump', 'submit + GPU', 'present pacing')
    hypothesisShellReconcileFanOut = $(if (-not $diagBuild) { 'insufficientData' } elseif ($renderBudget -gt 0) { 'confirmed' } else { 'refuted' })
    hypothesisMaximizeGpuFillBound = 'insufficientData'
  }
}

WriteJsonNoBom $summary (Join-Path $Session 'feel-summary.json')
Copy-Item (Join-Path $PSScriptRoot 'AGENT.md') (Join-Path $Session 'AGENT.md') -Force

Write-Host ""
if (-not $trusted) {
  Write-Host "BUNDLE IS NOT TRUSTWORTHY - do not rank anything from it:" -ForegroundColor Red
  foreach ($r in $hardFail) { Write-Host "  * $r" -ForegroundColor Red }
}
else {
  Step "Validity"
  Info "build $($summary.validity.buildFlavor)  [fps] $($fpsLines.Count) (scroll-active $($scrollOnly.Count))  csv rows $($csvRows.Count)  latency rows $($latRows.Count)"
  foreach ($r in $untrusted) { Warn $r }
  if ($null -ne $reproduced -and -not $reproduced) {
    Write-Host "    Every scored phase came back 4-5: THE SESSION DID NOT REPRODUCE THE COMPLAINT." -ForegroundColor Yellow
    Write-Host "    That is a successful run. Report it and re-capture when the problem is present." -ForegroundColor Yellow
  }
  Step "Ranked likely contributors (multi-label; totals may exceed 100%)"
  if ($noDominant) { Info "none - noDominantStage. The tool declines to name a suspect." }
  else { foreach ($r in $ranked) { Info $r } }
}
Write-Host ""
Step "Wrote feel-summary.json + AGENT.md into $Session"
