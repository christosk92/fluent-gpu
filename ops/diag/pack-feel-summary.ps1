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

# Robust centre. Used to DERIVE the refresh period from a phase's own present intervals, so the idle-gap cutoff
# scales with the panel instead of being a constant that silently means a different number of refreshes per device.
function Median([double[]]$v) {
  if (-not $v -or $v.Count -eq 0) { return $null }
  $s = $v | Sort-Object
  return $s[[int][math]::Floor(0.5 * ($s.Count - 1))]
}

# ScrollTrace writes an EMPTY field for an exact 0 (it is a size optimisation over millions of rows), and several
# of its columns are deliberately SIGNED — frameOverrun, clockSampleSkew, lagDip. So the field must be parsed, not
# string-prefixed: "0" + "-6.2" is not a number, and a naive prefix trick throws on exactly the negative values
# that carry the most diagnostic weight (headroom, and a clock sampled behind the frame).
function Num($s) {
  if ($null -eq $s -or "$s".Trim().Length -eq 0) { return 0.0 }
  $d = 0.0
  if ([double]::TryParse("$s", [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { return $d }
  throw "Invalid invariant-culture number '$s' while packing $Session"
}

# Nullable numeric parse for fields where blank means NOT MEASURED rather than the trace ring's compact spelling of
# an exact zero. Keep this separate from Num: most POD columns intentionally encode zero as blank, while external
# PresentMon NA fields and legacy clock-skew rows must never be silently promoted into measured zeroes.
function TryNum($s) {
  if ($null -eq $s -or "$s".Trim().Length -eq 0 -or "$s" -eq 'NA') { return $null }
  $d = 0.0
  if ([double]::TryParse("$s", [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { return $d }
  return $null
}

function Pct($part, $whole) {
  if ($whole -le 0) { return $null }
  return [math]::Round(100.0 * $part / $whole, 2)
}

# A clock-skew sample exists only when a drag frame actually resampled contact input. New traces carry that fact in
# latency i1 bit 24, so a valid exact-zero f3 stays distinguishable from an unmeasured blank f3. Legacy traces have no
# marker; their safe fallback is drag + a non-blank f3. That necessarily drops legacy exact zeroes, which is preferable
# to manufacturing thousands of zero samples from wheel/idle rows that sampled nothing.
$TrackingSampleValidBit = 1 -shl 24
function SelectTrackingRows($rows, [bool]$explicitMarker, [bool]$stateColumnPresent) {
  return @($rows | Where-Object {
      if ($explicitMarker) { return (([int]$_.i1 -band $TrackingSampleValidBit) -ne 0) }
      if (-not $stateColumnPresent -or (StateGesture $_.state) -ne 1) { return $false }
      return -not [string]::IsNullOrWhiteSpace("$($_.f3)")
    })
}

function SkewSummary($trackingRows, [bool]$explicitMarker, [double]$refreshPeriodMs) {
  if ($trackingRows.Count -eq 0) {
    return NotMeasured $(if ($explicitMarker) { 'no drag latency row carried trackingSampleValid' } else { 'legacy trace: no drag latency row carried a non-blank clockSampleSkewMs' })
  }

  [double[]]$values = @($trackingRows | ForEach-Object { Num $_.f3 })

  # One-millisecond bins tolerate sub-tick jitter. The reported mode is the median of the winning bin rather than
  # its arbitrary centre, retaining the meaningful -20.333 ms observed on a 120 Hz + 12 ms resample path.
  $bins = @{}
  foreach ($v in $values) {
    $lower = [math]::Floor($v)
    $key = $lower.ToString('0', [System.Globalization.CultureInfo]::InvariantCulture)
    if (-not $bins.ContainsKey($key)) { $bins[$key] = New-Object System.Collections.ArrayList }
    [void]$bins[$key].Add($v)
  }
  $orderedBins = @($bins.Keys | ForEach-Object {
      $a = [double[]]@($bins[$_])
      $lower = [double]::Parse($_, [System.Globalization.CultureInfo]::InvariantCulture)
      [pscustomobject]@{ lowerMs = $lower; upperExclusiveMs = $lower + 1.0; count = $a.Count; values = $a }
    } | Sort-Object @{ Expression = 'count'; Descending = $true }, @{ Expression = 'lowerMs'; Descending = $false })
  $winning = $orderedBins[0]
  $modeMs = Median $winning.values
  $concentrated = @($values | Where-Object { [math]::Abs($_ - $modeMs) -le 1.0 }).Count
  # "One refresh late" moves this signed value TOWARD zero (less negative), never farther negative. Preserve that
  # direction explicitly: an absolute-value test reverses the diagnosis around the expected -20 ms mode. The split
  # scales with the phase's measured refresh; the old fixed +6 ms happened to be 0.72R at 120 Hz but was wrong at
  # 60/240 Hz. Seventy percent of R stays well beyond modal jitter while retaining the one-slot cluster.
  if ([double]::IsNaN($refreshPeriodMs) -or [double]::IsInfinity($refreshPeriodMs) -or $refreshPeriodMs -le 0.0) {
    $refreshPeriodMs = 16.67
  }
  $lateOffsetMs = 0.70 * $refreshPeriodMs
  $lateThresholdMs = $modeMs + $lateOffsetMs
  $late = @($values | Where-Object { $_ -gt $lateThresholdMs }).Count
  $histogram = @($orderedBins | ForEach-Object {
      [ordered]@{ lowerMs = $_.lowerMs; upperExclusiveMs = $_.upperExclusiveMs; count = $_.count }
    })
  $stats = Stats $values
  foreach ($k in @('selection', 'trackingSampleValidMarker', 'modeMs', 'modalConcentrationWithin1MsCount',
                    'modalConcentrationWithin1MsPct', 'refreshPeriodMs', 'oneRefreshLateThresholdOffsetMs',
                    'oneRefreshLateThresholdMs', 'oneRefreshLateTailCount',
                    'oneRefreshLateTailPct', 'histogramBinWidthMs', 'histogram')) {
    # Reserve the keys before assigning below so ConvertTo-Json retains a stable, readable order after the common
    # Stats fields used by existing consumers.
    $stats[$k] = $null
  }
  $stats.selection = $(if ($explicitMarker) { 'trackingSampleValid=i1.bit24 (authoritative contact provenance)' } else { 'gesture=drag AND legacy f3 nonblank' })
  $stats.trackingSampleValidMarker = $(if ($explicitMarker) { 'explicit:i1.bit24' } else { 'legacy:f3-nonblank-fallback' })
  $stats.modeMs = [math]::Round($modeMs, 3)
  $stats.modalConcentrationWithin1MsCount = $concentrated
  $stats.modalConcentrationWithin1MsPct = Pct $concentrated $values.Count
  $stats.refreshPeriodMs = [math]::Round($refreshPeriodMs, 3)
  $stats.oneRefreshLateThresholdOffsetMs = [math]::Round($lateOffsetMs, 3)
  $stats.oneRefreshLateThresholdMs = [math]::Round($lateThresholdMs, 3)
  $stats.oneRefreshLateTailCount = $late
  $stats.oneRefreshLateTailPct = Pct $late $values.Count
  $stats.histogramBinWidthMs = 1.0
  $stats.histogram = $histogram
  return $stats
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
$anchorQpc = $null; $qpcFreq = $null; $anchorWall = $null; $traceArmed = $null; $anchorPid = $null
if ($anchorLine) {
  if ($anchorLine -match 'qpc=(\d+)') { $anchorQpc = [long]$Matches[1] }
  if ($anchorLine -match 'qpcFreq=(\d+)') { $qpcFreq = [long]$Matches[1] }
  if ($anchorLine -match 'wallUtc=(\S+)') { $anchorWall = $Matches[1] }
  if ($anchorLine -match 'trace=(\d)') { $traceArmed = ($Matches[1] -eq '1') }
  if ($anchorLine -match 'pid=(\d+)') { $anchorPid = [int]$Matches[1] }
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
  if ($l -match 'tMs=([0-9.]+)') { $rec.tMs = Num $Matches[1] }
  # The ScrollActive marker is the literal ' scroll loop ' the host prints before the loop-fps token. A bare
  # \bscroll\b ALSO matches the ' | scroll clipE=' scroll-perf token, which appears on every [fps] line whenever
  # FG_SCROLL_PERF is set - so with that flag on, "scroll-active" silently became "all frames" and every per-scroll
  # statistic was computed over idle time as well.
  $rec.scroll = ($l -match ' scroll loop ')
  $rec.spike = ($l -match '\bSPIKE\b')
  if ($l -match 'loop (\d+)fps ([0-9.]+)ms') { $rec.loopFps = [int]$Matches[1]; $rec.frameMs = Num $Matches[2] }
  if ($l -match 'presentD=(\d+)') { $rec.presentDelta = [int]$Matches[1] }
  if ($l -match 'pubD=(\d+)') { $rec.publishDelta = [int]$Matches[1] }
  if ($l -match 'coal=(\d+)') { $rec.coalesced = [int]$Matches[1] }
  if ($l -match 'lag=(\d+)') { $rec.renderLagFrames = [int]$Matches[1] }
  if ($l -match 'skipD=(\d+)') { $rec.skipDelta = [int]$Matches[1] }
  if ($l -match 'gpu ([0-9.]+)ms') { $rec.fenceWaitMs = Num $Matches[1] }
  if ($l -match 'latW([0-9.]+)(?:ms)?') { $rec.latencyWaitMs = Num $Matches[1] }
  # Always-on whole-frame GPU EXECUTION span. The sequence belongs to the completed timestamp sample, not this log
  # line: skip-submit/idle lines can repeat it, so consumers must deduplicate by sequence before computing stats.
  if ($l -match 'gexec ([0-9.]+)ms#(\d+)') {
    $rec.gpuExecutionMs = Num $Matches[1]
    $rec.gpuExecutionSeq = [uint64]$Matches[2]
  }
  if ($l -match 'gexecAge=(\d+)') { $rec.gpuExecutionSubmitAge = [uint64]$Matches[1] }
  # Optional FG_GPU_TIMING category profiler. Kept separate from gexec: this token exists only on a fresh category
  # resolve and pays the high-cardinality query cost that the always-on whole-frame pair deliberately avoids.
  if ($l -match 'grender ([0-9.]+)ms') { $rec.gpuCategoryRenderMs = Num $Matches[1] }
  if ($l -match 'wait ([a-z-]+)(-?\d+)') { $rec.waitKind = $Matches[1]; $rec.waitMs = [int]$Matches[2] }
  if ($l -match 'smiss=gd(\d+)/sb(\d+)/ed(\d+)/ek(\d+)/ec(\d+)/cap(\d+)/mg(\d+)/mk(\d+)/geo(\d+)/mc(\d+)/mp(\d+)') {
    $rec.spanMiss = [ordered]@{
      globalDisabled = [int]$Matches[1]; scopedBlocked = [int]$Matches[2]
      exactDirty = [int]$Matches[3]; exactKey = [int]$Matches[4]; exactClip = [int]$Matches[5]
      exactCapacity = [int]$Matches[6]; moveGuard = [int]$Matches[7]; moveKey = [int]$Matches[8]
      moveGeometry = [int]$Matches[9]; moveClip = [int]$Matches[10]; movePayload = [int]$Matches[11]
    }
  }
  if ($l -match '\brq(\d+)/(\d+)') {
    $rec.rectOpaqueInstances = [int]$Matches[1]
    $rec.rectBlendedInstances = [int]$Matches[2]
  }
  if ($l -match 'rareaMp=([0-9.]+)/([0-9.]+)') {
    $rec.rectOpaqueSubmittedMp = Num $Matches[1]
    $rec.rectBlendedSubmittedMp = Num $Matches[2]
  }
  if ($l -match 'rareaSeq=(\d+)') { $rec.rectAreaSeq = [uint64]$Matches[1] }
  if ($l -match 'btop=([^ ]+)') {
    $top = New-Object System.Collections.ArrayList
    foreach ($entry in $Matches[1].Split(',')) {
      if ($entry -notmatch '^(\d+):([0-9.]+):([0-9.]+):([0-9.]+)x([0-9.]+):([0-9A-Fa-f]+)$') { continue }
      [void]$top.Add([pscustomobject]@{
        Ordinal = [int]$Matches[1]; AreaMp = Num $Matches[2]; EffectiveAlpha = Num $Matches[3]
        LocalW = Num $Matches[4]; LocalH = Num $Matches[5]; Flags = [Convert]::ToInt32($Matches[6], 16)
      })
    }
    $rec.blendedTop = @($top)
  }
  if ($l -match '@(\d+)Hz') { $rec.panelHz = [int]$Matches[1] }
  if ($rec.Contains('tMs')) { $scrollFps += $rec }
}
$scrollOnly = @($scrollFps | Where-Object { $_.scroll })

# rarea/rq is a backend-completed snapshot that can repeat on several UI-side FPS lines. Keep exactly its first
# observation per target-local rareaSeq; that both removes stale repetition and prevents an idle-first snapshot from
# being reassigned to a later scroll line. Older logs have no sequence, so retain their line observations but label
# that fallback explicitly in the summary.
$rectSnapshotBySeq = @{}
$rectSnapshotUnique = New-Object System.Collections.ArrayList
$rectSnapshotLegacy = New-Object System.Collections.ArrayList
foreach ($r in @($scrollFps | Sort-Object { [double]$_['tMs'] })) {
  $hasRectSnapshot = $r.Contains('rectOpaqueInstances') -or $r.Contains('rectOpaqueSubmittedMp')
  if (-not $hasRectSnapshot) { continue }
  if ($r.Contains('rectAreaSeq')) {
    $key = ([uint64]$r.rectAreaSeq).ToString([System.Globalization.CultureInfo]::InvariantCulture)
    if ($rectSnapshotBySeq.ContainsKey($key)) { continue }
    $rectSnapshotBySeq[$key] = $r
    [void]$rectSnapshotUnique.Add($r)
  }
  else { [void]$rectSnapshotLegacy.Add($r) }
}
$rectSnapshotsAll = @($rectSnapshotUnique) + @($rectSnapshotLegacy)
$rectSnapshotsScroll = @($rectSnapshotUnique | Where-Object { $_.scroll }) + @($rectSnapshotLegacy | Where-Object { $_.scroll })

function FpsMetricSummary($rows, $field, $reason, $semantics) {
  [double[]]$values = @($rows | Where-Object { $_.Contains($field) } | ForEach-Object { [double]$_[$field] })
  return [ordered]@{
    semantics = $semantics
    lineObservationCount = $values.Count
    stats = $(if ($values.Count -gt 0) { Stats $values } else { NotMeasured $reason })
  }
}

function WaitKindSummary($rows, $scope) {
  $observed = @($rows | Where-Object { $_.Contains('waitKind') })
  $kinds = @($observed | Group-Object { $_.waitKind } | Sort-Object Count -Descending | ForEach-Object {
      [double[]]$timeouts = @($_.Group | Where-Object { $_.Contains('waitMs') } | ForEach-Object { [double]$_.waitMs })
      [ordered]@{
        kind = $_.Name
        lineCount = $_.Count
        requestedTimeoutMs = $(if ($timeouts.Count -gt 0) { Stats $timeouts } else { NotMeasured 'wait timeout token absent' })
        requestedTimeoutMsSum = $(if ($timeouts.Count -gt 0) { [math]::Round((($timeouts | Measure-Object -Sum).Sum), 3) } else { $null })
      }
    })
  return [ordered]@{
    scope = $scope
    semantics = 'line observations of the requested wait; timeout sums are upper bounds, not measured sleep duration (input can wake early)'
    observedLineCount = $observed.Count
    adaptiveGpuLineCount = @($observed | Where-Object { $_.waitKind -eq 'adaptive-gpu' }).Count
    histogram = $kinds
  }
}

function SpanMissSummary($rows, $scope) {
  $observed = @($rows | Where-Object { $_.Contains('spanMiss') })
  $fields = [ordered]@{
    globalDisabled = 'globalDisabled'; scopedBlocked = 'scopedBlocked'; exactDirty = 'exactDirty'
    exactKey = 'exactKey'; exactClip = 'exactClip'; exactCapacity = 'exactCapacity'
    moveGuard = 'moveGuard'; moveKey = 'moveKey'; moveGeometry = 'moveGeometry'
    moveClip = 'moveClip'; movePayload = 'movePayload'
  }
  $reasons = [ordered]@{}
  foreach ($name in $fields.Keys) {
    [double[]]$values = @($observed | ForEach-Object { [double]$_.spanMiss[$fields[$name]] })
    $reasons[$name] = [ordered]@{
      sumAcrossObservedFrames = $(if ($values.Count -gt 0) { [long](($values | Measure-Object -Sum).Sum) } else { NotMeasured 'no smiss token in this scope' })
      perObservedFrame = $(if ($values.Count -gt 0) { Stats $values } else { NotMeasured 'no smiss token in this scope' })
    }
  }
  return [ordered]@{
    scope = $scope
    semantics = 'diagnostic gate-decline census only; overlapping counters are not a causal attribution and require bisection before a product claim'
    observedLineCount = $observed.Count
    reasons = $reasons
  }
}

function RectAreaSummary($rawRows, $summarizedRows, $scope) {
  $observed = @($rawRows | Where-Object {
      $_.Contains('rectOpaqueInstances') -or $_.Contains('rectOpaqueSubmittedMp')
    })
  $summarized = @($summarizedRows | Where-Object {
      $_.Contains('rectOpaqueInstances') -or $_.Contains('rectOpaqueSubmittedMp')
    })
  $rawSequenced = @($observed | Where-Object { $_.Contains('rectAreaSeq') })
  $legacy = @($observed | Where-Object { -not $_.Contains('rectAreaSeq') })
  $rawDistinctSeqCount = @($rawSequenced | Group-Object { [string]$_.rectAreaSeq }).Count
  $summarizedSequencedCount = @($summarized | Where-Object { $_.Contains('rectAreaSeq') }).Count
  [double[]]$opaque = @($summarized | Where-Object { $_.Contains('rectOpaqueSubmittedMp') } |
    ForEach-Object { [double]$_.rectOpaqueSubmittedMp })
  [double[]]$blended = @($summarized | Where-Object { $_.Contains('rectBlendedSubmittedMp') } |
    ForEach-Object { [double]$_.rectBlendedSubmittedMp })
  [double[]]$opaqueInstances = @($summarized | Where-Object { $_.Contains('rectOpaqueInstances') } |
    ForEach-Object { [double]$_.rectOpaqueInstances })
  [double[]]$blendedInstances = @($summarized | Where-Object { $_.Contains('rectBlendedInstances') } |
    ForEach-Object { [double]$_.rectBlendedInstances })
  [double[]]$blendedFractions = @($summarized | Where-Object {
      $_.Contains('rectOpaqueInstances') -and $_.Contains('rectBlendedInstances') -and
      ([double]$_.rectOpaqueInstances + [double]$_.rectBlendedInstances) -gt 0
    } | ForEach-Object {
      [double]$_.rectBlendedInstances / ([double]$_.rectOpaqueInstances + [double]$_.rectBlendedInstances)
    })
  $candidateMap = @{}
  foreach ($r in $summarized) {
    if (-not $r.Contains('blendedTop')) { continue }
    foreach ($x in @($r.blendedTop)) {
      $w = ([double]$x.LocalW).ToString('0.###', [Globalization.CultureInfo]::InvariantCulture)
      $h = ([double]$x.LocalH).ToString('0.###', [Globalization.CultureInfo]::InvariantCulture)
      $a = ([double]$x.EffectiveAlpha).ToString('0.###', [Globalization.CultureInfo]::InvariantCulture)
      $key = "${w}x${h}|a${a}|f$([int]$x.Flags)"
      if (-not $candidateMap.ContainsKey($key)) {
        $candidateMap[$key] = [pscustomobject]@{
          signature = $key; localWDip = [double]$x.LocalW; localHDip = [double]$x.LocalH
          effectiveAlpha = [double]$x.EffectiveAlpha; flags = [int]$x.Flags
          observationCount = 0; areaMpSum = 0.0; areaMpMax = 0.0; firstOrdinal = [int]$x.Ordinal
        }
      }
      $c = $candidateMap[$key]
      $c.observationCount++
      $c.areaMpSum += [double]$x.AreaMp
      $c.areaMpMax = [math]::Max($c.areaMpMax, [double]$x.AreaMp)
      $c.firstOrdinal = [math]::Min($c.firstOrdinal, [int]$x.Ordinal)
    }
  }
  $candidates = @($candidateMap.Values | ForEach-Object {
      [ordered]@{
        signature = $_.signature; localWDip = $_.localWDip; localHDip = $_.localHDip
        effectiveAlpha = $_.effectiveAlpha; flags = $_.flags; observationCount = $_.observationCount
        meanAreaMp = $(if ($_.observationCount -gt 0) { [math]::Round($_.areaMpSum / $_.observationCount, 3) } else { 0.0 })
        maxAreaMp = [math]::Round($_.areaMpMax, 3); firstOrdinal = $_.firstOrdinal
      }
    } | Sort-Object maxAreaMp, observationCount -Descending | Select-Object -First 32)
  return [ordered]@{
    scope = $scope
    semantics = 'rq instance counts plus submitted nominal transformed rect area in physical megapixels; overlap/clipping are not removed, so area is painter work, not screen coverage'
    observedLineCount = $observed.Count
    summarizedSnapshotCount = $summarized.Count
    deduplication = [ordered]@{
      mode = $(if ($rawSequenced.Count -gt 0 -and $legacy.Count -eq 0) {
          'rareaSeq: one first-observed backend snapshot per target-local sequence'
        } elseif ($rawSequenced.Count -eq 0 -and $legacy.Count -gt 0) {
          'legacy unsequenced FPS line observations; stale repeats cannot be removed'
        } elseif ($rawSequenced.Count -gt 0) {
          'mixed: rareaSeq rows deduplicated; legacy rows remain line observations'
        } else { 'not measured' })
      sequenceTaggedLineCount = $rawSequenced.Count
      distinctSequenceCountObservedInScope = $rawDistinctSeqCount
      firstObservedSequenceCountAttributedToScope = $summarizedSequencedCount
      repeatedOrFirstObservedOutsideScopeCount = $rawSequenced.Count - $summarizedSequencedCount
      legacyUnsequencedLineCount = $legacy.Count
    }
    opaqueInstances = $(if ($opaqueInstances.Count -gt 0) { Stats $opaqueInstances } else { NotMeasured 'no rq token in this scope' })
    blendedInstances = $(if ($blendedInstances.Count -gt 0) { Stats $blendedInstances } else { NotMeasured 'no rq token in this scope' })
    blendedInstanceFraction = $(if ($blendedFractions.Count -gt 0) { Stats $blendedFractions } else { NotMeasured 'no rq snapshot with at least one submitted rect in this scope' })
    opaqueSubmittedMp = $(if ($opaque.Count -gt 0) { Stats $opaque } else { NotMeasured 'no rareaMp token (FG_RENDER_DIAG off or older build)' })
    blendedSubmittedMp = $(if ($blended.Count -gt 0) { Stats $blended } else { NotMeasured 'no rareaMp token (FG_RENDER_DIAG off or older build)' })
    blendedTopCandidateFlags = '1=rounded, 2=stroked, 4=roundedClip, 8=nonPlainKind; signatures are geometry/alpha clues, not scene-node identity'
    repeatedTopCandidates = @($candidates)
  }
}

# Sequence-aware gexec inventory. A sequence is counted exactly once, at its FIRST log observation; later appearances
# are stale repeats, not more GPU work. This is intentionally independent of the line-observation distributions for
# `gpu` (frame-fence wall wait), `latW` (DXGI latency waitable), and `grender` (opt-in category attribution).
$gpuExecutionBySeq = @{}
$gpuExecutionUnique = New-Object System.Collections.ArrayList
$gpuExecutionRawTokens = 0; $gpuExecutionTokensWithoutAge = 0; $gpuExecutionConflicts = 0; $gpuExecutionNonMonotonic = 0
$gpuExecutionAgeRegressions = 0
$gpuExecutionRepeatAfterNewer = 0; [long]$gpuExecutionUnobservedSequenceGaps = 0; $lastNewGpuExecutionSeq = $null
foreach ($r in @($scrollFps | Sort-Object { [double]$_['tMs'] })) {
  if (-not $r.Contains('gpuExecutionSeq')) { continue }
  $gpuExecutionRawTokens++
  [uint64]$seq = $r.gpuExecutionSeq
  $key = $seq.ToString([System.Globalization.CultureInfo]::InvariantCulture)
  if ($gpuExecutionBySeq.ContainsKey($key)) {
    $prior = $gpuExecutionBySeq[$key]
    if ([math]::Abs($prior.Ms - [double]$r.gpuExecutionMs) -gt 0.0001) { $gpuExecutionConflicts++ }
    if ($r.Contains('gpuExecutionSubmitAge')) {
      [uint64]$age = $r.gpuExecutionSubmitAge
      if ($null -ne $prior.MaxObservedSubmitAge -and $age -lt [uint64]$prior.MaxObservedSubmitAge) { $gpuExecutionAgeRegressions++ }
      if ($null -eq $prior.MaxObservedSubmitAge -or $age -gt [uint64]$prior.MaxObservedSubmitAge) { $prior.MaxObservedSubmitAge = $age }
    }
    else { $gpuExecutionTokensWithoutAge++ }
    if ($null -ne $lastNewGpuExecutionSeq -and $seq -lt [uint64]$lastNewGpuExecutionSeq) { $gpuExecutionRepeatAfterNewer++ }
    continue
  }
  if ($null -ne $lastNewGpuExecutionSeq) {
    [uint64]$lastSeq = [uint64]$lastNewGpuExecutionSeq
    if ($seq -lt $lastSeq) { $gpuExecutionNonMonotonic++ }
    elseif ($seq -gt ($lastSeq + [uint64]1)) { $gpuExecutionUnobservedSequenceGaps += [long]($seq - $lastSeq - [uint64]1) }
  }
  $firstAge = $null
  if ($r.Contains('gpuExecutionSubmitAge')) { $firstAge = [uint64]$r.gpuExecutionSubmitAge }
  else { $gpuExecutionTokensWithoutAge++ }
  $sample = [pscustomobject]@{
    Seq = $seq
    Ms = [double]$r.gpuExecutionMs
    FirstTMs = $(if ($r.Contains('tMs')) { [double]$r.tMs } else { $null })
    FirstObservedDuringScroll = [bool]$r.scroll
    FirstObservedSubmitAge = $firstAge
    MaxObservedSubmitAge = $firstAge
  }
  $gpuExecutionBySeq[$key] = $sample
  [void]$gpuExecutionUnique.Add($sample)
  $lastNewGpuExecutionSeq = $seq
}
[double[]]$gpuExecutionAllValues = @($gpuExecutionUnique | ForEach-Object { $_.Ms })
$gpuExecutionMaxPolicySubmitAge = 6   # mirrors AppHost.GpuGovernorMaxSubmitAge; age is emitted beside every current gexec token
[double[]]$gpuExecutionFirstAges = @($gpuExecutionUnique | Where-Object { $null -ne $_.FirstObservedSubmitAge } | ForEach-Object { [double]$_.FirstObservedSubmitAge })
[double[]]$gpuExecutionScrollValues = @($gpuExecutionUnique | Where-Object {
    $_.FirstObservedDuringScroll -and $null -ne $_.FirstObservedSubmitAge -and
    [uint64]$_.FirstObservedSubmitAge -le [uint64]$gpuExecutionMaxPolicySubmitAge
  } | ForEach-Object { $_.Ms })
$gpuExecutionScrollTooOld = @($gpuExecutionUnique | Where-Object {
    $_.FirstObservedDuringScroll -and $null -ne $_.FirstObservedSubmitAge -and
    [uint64]$_.FirstObservedSubmitAge -gt [uint64]$gpuExecutionMaxPolicySubmitAge
  }).Count
$gpuExecutionScrollUnknownAge = @($gpuExecutionUnique | Where-Object {
    $_.FirstObservedDuringScroll -and $null -eq $_.FirstObservedSubmitAge
  }).Count
$gpuExecutionSummary = [ordered]@{
  semantics = 'whole-frame GPU execution timestamp pair; unique completed samples only, never frame-fence wall wait'
  rawTokenCount = $gpuExecutionRawTokens
  uniqueSampleCount = $gpuExecutionUnique.Count
  staleRepeatTokenCount = $gpuExecutionRawTokens - $gpuExecutionUnique.Count
  tokenWithoutSubmitAgeCount = $gpuExecutionTokensWithoutAge
  conflictingValueForSameSequenceCount = $gpuExecutionConflicts
  monotonicFirstObservationOrder = ($gpuExecutionNonMonotonic -eq 0)
  monotonicTokenOrderIgnoringAdjacentRepeats = ($gpuExecutionNonMonotonic -eq 0 -and $gpuExecutionRepeatAfterNewer -eq 0)
  nonMonotonicNewSequenceCount = $gpuExecutionNonMonotonic
  repeatAfterNewerSequenceCount = $gpuExecutionRepeatAfterNewer
  submitAgeRegressionTokenCount = $gpuExecutionAgeRegressions
  unobservedSequenceGapCount = $gpuExecutionUnobservedSequenceGaps
  firstSequence = $(if ($gpuExecutionUnique.Count -gt 0) { [string]$gpuExecutionUnique[0].Seq } else { $null })
  lastFirstObservedSequence = $(if ($gpuExecutionUnique.Count -gt 0) { [string]$gpuExecutionUnique[$gpuExecutionUnique.Count - 1].Seq } else { $null })
  policyMaxSubmitAge = $gpuExecutionMaxPolicySubmitAge
  firstObservedSubmitAge = $(if ($gpuExecutionFirstAges.Count -gt 0) { Stats $gpuExecutionFirstAges } else { NotMeasured 'gexecAge is absent (older token schema)' })
  scrollFirstObservedTooOldCount = $gpuExecutionScrollTooOld
  scrollFirstObservedUnknownAgeCount = $gpuExecutionScrollUnknownAge
  allUniqueSamplesMs = $(if ($gpuExecutionAllValues.Count -gt 0) { Stats $gpuExecutionAllValues } else { NotMeasured 'no gexec token in [fps] lines (older build or no completed whole-frame timestamp sample)' })
  scrollFirstObservedUniqueSamplesMs = $(if ($gpuExecutionScrollValues.Count -gt 0) { Stats $gpuExecutionScrollValues } else { NotMeasured 'no unique gexec sample was first observed on a scroll-active line within the governor submit-age bound (unknown/old samples are not attributed to that scroll)' })
}
$gpuTimingSummary = [ordered]@{
  fenceWaitMs = [ordered]@{
    allFps = FpsMetricSummary $scrollFps 'fenceWaitMs' 'no legacy gpu token in [fps] lines' 'wall wait on frame/backbuffer retirement; not shader/raster time'
    scrollActiveFps = FpsMetricSummary $scrollOnly 'fenceWaitMs' 'no scroll-active legacy gpu token' 'line observations; may include queue retirement'
  }
  dxgiLatencyWaitMs = [ordered]@{
    allFps = FpsMetricSummary $scrollFps 'latencyWaitMs' 'no latW token in [fps] lines' 'DXGI frame-latency waitable only'
    scrollActiveFps = FpsMetricSummary $scrollOnly 'latencyWaitMs' 'no scroll-active latW token' 'line observations'
  }
  gpuExecutionMs = $gpuExecutionSummary
  categoryProfilerRenderMs = [ordered]@{
    allFps = FpsMetricSummary $scrollFps 'gpuCategoryRenderMs' 'no grender token (FG_GPU_TIMING was off or produced no fresh category sample)' 'FG_GPU_TIMING whole/category resolve; optional and higher overhead'
    scrollActiveFps = FpsMetricSummary $scrollOnly 'gpuCategoryRenderMs' 'no scroll-active grender token' 'fresh category-profiler line observations only'
  }
}
$waitPolicySummary = [ordered]@{
  allFps = WaitKindSummary $scrollFps 'all logged [fps] lines'
  scrollActiveFps = WaitKindSummary $scrollOnly 'scroll-active [fps] lines'
}
$renderDiagnosticsSummary = [ordered]@{
  spanReuseMisses = [ordered]@{
    allFps = SpanMissSummary $scrollFps 'all logged [fps] lines carrying smiss'
    scrollActiveFps = SpanMissSummary $scrollOnly 'scroll-active [fps] lines carrying smiss'
  }
  submittedRectArea = [ordered]@{
    allFps = RectAreaSummary $scrollFps $rectSnapshotsAll 'all logged [fps] lines carrying rq and/or rareaMp'
    scrollActiveFps = RectAreaSummary $scrollOnly $rectSnapshotsScroll 'scroll-active [fps] lines carrying rq and/or rareaMp; sequenced snapshots are attributed only where first observed'
  }
}

# ── observer cost: this bundle's frame time vs the paired plain-Release arm ───────────────────────────────────
# Compared on the MEDIAN, not the mean: frame-time distributions have a long right tail, and one 500 ms outlier in
# either arm would swamp a mean and invent (or hide) an observer effect that is not there.
# Prefer SCROLL-ACTIVE lines: observer cost is only meaningful over the workload being measured. An idle window
# does almost no work per frame, so comparing idle medians measures the printing resolution, not the instrument.
function MedianFrameMs($lines) {
  $active = @($lines | Where-Object { $_ -match ' scroll loop ' })   # precise marker; see the $rec.scroll note above
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
$hasExplicitTrackingSampleMarker = @($latRows | Where-Object { (([int]$_.i1 -band $TrackingSampleValidBit) -ne 0) }).Count -gt 0

$gestureNames = @('idle', 'drag', 'ballistic', 'driven')
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
  # Cold-pass bit (state bit 6) is excluded. The old guided protocol stamped repetition 1 as cold. Free-scroll
  # stamps cold=0 for the whole session, so this filter does not drop the capture.
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
  # IDLE-GAP EXCLUSION. A latency row is emitted per scroll-active frame, and missedVsyncs is derived from the
  # interval since the previous PRESENT. The first row after a pause therefore measures the pause, not a stall:
  # an 18 s gap between gestures reported 2194 "missed slots" in a real bundle, which is not a hitch, it is a
  # human reading the screen. The cutoff is DERIVED from this phase's own observed cadence rather than hardcoded:
  # the median present interval is the measured refresh period (robust to the very outliers being excluded), and six
  # of them is comfortably past any real stall while still well inside a human pause. The previous 100 ms constant
  # claimed to be "six refreshes" but was twelve on a 120 Hz panel and six on a 60 Hz one.
  $allIntervals = @($warm | ForEach-Object { (Num $_.f4) } | Where-Object { $_ -gt 0 })
  $refreshMs = $(if ($allIntervals.Count -ge 5) { (Median $allIntervals) } else { 16.67 })
  $gapCutoffMs = [math]::Round([math]::Max(50.0, $refreshMs * 6.0), 2)
  # Applied CONSISTENTLY: a sample excluded from the derived count must also be excluded from the attested count
  # and from the interval distribution, or the three disagree about which frames the phase contains.
  $inWindow = @($warm | Where-Object { $null -eq (Num $_.f4) -or (Num $_.f4) -le $gapCutoffMs })
  $missedGapsDropped = $warm.Count - $inWindow.Count
  $missed = @($inWindow | ForEach-Object { [int]$_.i2 -band 0xFFFF })
  $attestedRaw = @($inWindow | ForEach-Object { ([int]$_.i2 -shr 16) -band 0xFFFF } | Where-Object { $_ -gt 0 })
  $attestedSum = $null; $attestedMax = $null; $attestedFrames = $attestedRaw.Count
  if ($attestedFrames -gt 0) {
    $attestedSum = (($attestedRaw | ForEach-Object { $_ - 1 } | Measure-Object -Sum).Sum)
    $attestedMax = (($attestedRaw | ForEach-Object { $_ - 1 } | Measure-Object -Maximum).Maximum)
  }
  $neverPresented = @($warm | Where-Object { [int]$_.i0 -eq 0 }).Count
  $intervals = @($inWindow | Where-Object { (Num $_.f4) -gt 0 } | ForEach-Object { (Num $_.f4) })
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
  $trackingRows = @(SelectTrackingRows $warm $hasExplicitTrackingSampleMarker $hasState)
  $trackingInsufficient = ($trackingRows.Count -lt 20)
  $trackingInsufficientReason = $(if ($trackingInsufficient) {
      "only $($trackingRows.Count) warm frames actually resampled contact input (need >= 20; unmeasured blanks are not zeroes)"
    } else { $null })
  $skew = $(if ($trackingInsufficient) { NotMeasured $trackingInsufficientReason } else { SkewSummary $trackingRows $hasExplicitTrackingSampleMarker $refreshMs })

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
      trackingSampleFrames = $trackingRows.Count
      trackingInsufficientData = $trackingInsufficient
      trackingInsufficientDataReason = $trackingInsufficientReason
      # A sub-frame latency percentile off a `receive` stamp is a description of the producer's pump rate, not of
      # the input path. Refuse rather than publish it.
      inputToVblankOfPresentMs = $(if ($bestQuality -eq 'hardware') { $null } else { NotMeasured "genStampQuality=$bestQuality (below hardware)" })
      appliedVsIntendedDip = $(if ($trackingInsufficient) { NotMeasured $trackingInsufficientReason } else { Stats(@($trackingRows | ForEach-Object { (Num $_.f0) })) })
      velocityDipPerMs = $(if ($trackingInsufficient) { NotMeasured $trackingInsufficientReason } else { Stats(@($trackingRows | ForEach-Object { [math]::Abs((Num $_.f5)) })) })
      wakeOverheadMs = $(if ($insufficient) { NotMeasured $insufficientReason } else { Stats(@($warm | ForEach-Object { (Num $_.f1) })) })
      coalescingBiasNoted = $true
    }
    cadence = [ordered]@{
      presentIntervalMs = $intervalStats
      presentIntervalMsMeanPlus2Sd = $meanPlus2Sd
      missedVsyncsSum = (($missed | Measure-Object -Sum).Sum)
      missedVsyncsMax = (($missed | Measure-Object -Maximum).Maximum)
      missedVsyncsGapSamplesExcluded = $missedGapsDropped
      frameOverrunMs = $(if ($insufficient) { NotMeasured $insufficientReason } else { Stats(@($warm | ForEach-Object { (Num $_.f2) })) })
      clockSampleSkewMs = $skew
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

# ── PresentMon -> trace QPC join ────────────────────────────────────────────────────────────────────────────
# PresentMon's TimeInQPC and ScrollTrace's anchor are the only honest common clock. Do not line rows up by ordinal:
# both producers may drop or suppress frames. A PresentMon row inherits the latest trace-state stamp at or before its
# QPC, within the trace's observed time range (plus a small tail for Present returning after the final stamp). On the
# current schema every trace row carries the state word; using all of them catches begin/end edges between frame rows.
# A legacy CSV with no state COLUMN has no gesture witness at all and must remain unknown, never inferred as idle.
function PresentColumnCoverage($records, $name) {
  [double[]]$values = @($records | ForEach-Object {
      $prop = $_.Raw.PSObject.Properties[$name]
      if ($prop) { TryNum $prop.Value }
    } | Where-Object { $null -ne $_ })
  return [ordered]@{
    measuredCount = $values.Count
    missingCount = $records.Count - $values.Count
    coveragePct = Pct $values.Count $records.Count
    stats = $(if ($values.Count -gt 0) { Stats $values } else { NotMeasured "$name is NA or absent on every selected PresentMon row" })
  }
}

function PresentBucketSummary($records, $allIntervalCount, $predicate) {
  [double[]]$intervals = @($records | ForEach-Object { $_.IntervalMs })
  $joined = @($records | Where-Object { $_.GestureState -ge 0 })
  return [ordered]@{
    predicate = $predicate
    count = $records.Count
    pctOfMeasuredIntervals = Pct $records.Count $allIntervalCount
    gestureJoinedCount = $joined.Count
    gestureActiveCount = @($joined | Where-Object { $_.GestureState -gt 0 }).Count
    gestureIdleCount = @($joined | Where-Object { $_.GestureState -eq 0 }).Count
    gestureUnknownCount = $records.Count - $joined.Count
    intervalMs = $(if ($intervals.Count -gt 0) { Stats $intervals } else { NotMeasured 'no PresentMon interval matched this bucket' })
    gpuBusyMs = PresentColumnCoverage $records 'MsGPUBusy'
    gpuWaitMs = PresentColumnCoverage $records 'MsGPUWait'
    renderPresentLatencyMs = PresentColumnCoverage $records 'MsRenderPresentLatency'
  }
}

function PresentGestureSliceSummary($records, $scope) {
  [double[]]$intervals = @($records | Where-Object { $null -ne $_.IntervalMs -and $_.IntervalMs -ge 0 } |
    ForEach-Object { [double]$_.IntervalMs })
  return [ordered]@{
    scope = $scope
    rowCount = $records.Count
    intervalMs = $(if ($intervals.Count -gt 0) { Stats $intervals } else { NotMeasured "no measured present interval in $scope rows" })
    gpuBusyMs = PresentColumnCoverage $records 'MsGPUBusy'
    gpuWaitMs = PresentColumnCoverage $records 'MsGPUWait'
    gpuTimeMs = PresentColumnCoverage $records 'MsGPUTime'
    renderPresentLatencyMs = PresentColumnCoverage $records 'MsRenderPresentLatency'
  }
}

function PresentModeSliceSummary($records, $allRowCount) {
  return @($records |
    Where-Object { -not [string]::IsNullOrWhiteSpace("$($_.Raw.PresentMode)") } |
    Group-Object { $_.Raw.PresentMode } |
    Sort-Object Count -Descending |
    ForEach-Object {
      $mode = $_.Name
      $modeRows = @($_.Group)
      [ordered]@{
        mode = $mode
        rowCount = $modeRows.Count
        pctOfSelectedRows = Pct $modeRows.Count $allRowCount
        # Promotion/demotion can coincide with maximize, occlusion, or idle. Keep interaction state separate inside
        # each mode so an idle Composed frame is never pooled into an active Independent-Flip budget.
        gestureSlices = [ordered]@{
          all = PresentGestureSliceSummary $modeRows "PresentMode=$mode; every gesture state"
          active = PresentGestureSliceSummary @($modeRows | Where-Object { $_.GestureState -gt 0 }) "PresentMode=$mode; gesture active"
          idle = PresentGestureSliceSummary @($modeRows | Where-Object { $_.GestureState -eq 0 }) "PresentMode=$mode; gesture idle"
          unknown = PresentGestureSliceSummary @($modeRows | Where-Object { $_.GestureState -lt 0 }) "PresentMode=$mode; gesture unavailable/unjoined"
        }
      }
    })
}

$presentMonSummary = NotMeasured 'presentmon.csv missing'
if (Test-Path $pmPath) {
  try {
    $pmImported = @(Import-Csv $pmPath)
    $targetPid = $anchorPid
    if ($null -eq $targetPid -and $manifest -and $manifest.presentMon -and $null -ne $manifest.presentMon.targetProcessId) {
      $targetPid = [int]$manifest.presentMon.targetProcessId
    }

    $pmSelected = @()
    $pidFiltered = $false
    $applicationFallbackApplied = $false
    $hasProcessId = ($pmImported.Count -gt 0 -and ($pmImported[0].PSObject.Properties.Name -contains 'ProcessID'))
    $hasApplication = ($pmImported.Count -gt 0 -and ($pmImported[0].PSObject.Properties.Name -contains 'Application'))
    if ($null -ne $targetPid) {
      if (-not $hasProcessId) { throw 'target PID is known but presentmon.csv has no ProcessID column; cross-process rows cannot be selected safely' }
      $pmSelected = @($pmImported | Where-Object {
          $pidValue = TryNum $_.ProcessID
          $null -ne $pidValue -and [int]$pidValue -eq $targetPid
        })
      if ($pmSelected.Count -eq 0) { throw "presentmon.csv has no row for target PID $targetPid" }
      $pidFiltered = $true
    }
    elseif ($hasApplication) {
      # Legacy launcher manifests did not retain the target PID. The only safe fallback is an unambiguous Wavee.exe
      # application population; never silently aggregate the whole ETW session.
      $waveeRows = @($pmImported | Where-Object { "$(($_.Application))" -match '(?i)(^|[\\/])Wavee\.exe$' })
      if ($waveeRows.Count -eq 0) { throw 'target PID is absent and presentmon.csv has no Wavee.exe Application population' }
      if ($hasProcessId) {
        $waveePids = @($waveeRows | ForEach-Object { TryNum $_.ProcessID } | Where-Object { $null -ne $_ } |
          ForEach-Object { [int]$_ } | Sort-Object -Unique)
        if ($waveePids.Count -ne 1) { throw "target PID is absent and Wavee.exe rows span $($waveePids.Count) process IDs" }
        $targetPid = [int]$waveePids[0]
      }
      $pmSelected = $waveeRows
      $applicationFallbackApplied = $true
    }
    else {
      throw 'target PID is absent and presentmon.csv has no Application column for a safe Wavee.exe fallback'
    }

    $traceStates = @()
    if ($hasState) {
      $traceStates = @($csvRows | ForEach-Object {
          $tm = TryNum $_.tMs
          if ($null -ne $tm) { [pscustomobject]@{ TMs = $tm; State = [int](Num $_.state) } }
        } | Sort-Object TMs)
    }
    $canJoin = ($hasState -and $null -ne $anchorQpc -and $qpcFreq -gt 0 -and $traceStates.Count -gt 0 -and
                $pmSelected.Count -gt 0 -and ($pmSelected[0].PSObject.Properties.Name -contains 'TimeInQPC'))
    $tailToleranceMs = 100.0
    $frameAt = -1
    $pmObserved = New-Object System.Collections.ArrayList
    foreach ($row in @($pmSelected | Sort-Object { $v = TryNum $_.TimeInQPC; if ($null -eq $v) { [double]::PositiveInfinity } else { $v } })) {
      $qpc = TryNum $row.TimeInQPC
      $interval = TryNum $row.MsBetweenPresents
      $gesture = -1
      $timeMs = $null
      if ($canJoin -and $null -ne $qpc) {
        $timeMs = ($qpc - $anchorQpc) * 1000.0 / $qpcFreq
        while (($frameAt + 1) -lt $traceStates.Count -and $traceStates[$frameAt + 1].TMs -le $timeMs) { $frameAt++ }
        if ($frameAt -ge 0 -and $timeMs -ge $traceStates[0].TMs -and
            $timeMs -le ($traceStates[$traceStates.Count - 1].TMs + $tailToleranceMs)) {
          $gesture = StateGesture $traceStates[$frameAt].State
        }
      }
      $stream = "$($row.ProcessID)|$($row.SwapChainAddress)"
      [void]$pmObserved.Add([pscustomobject]@{
        Raw = $row; Qpc = $qpc; TMs = $timeMs; IntervalMs = $interval; GestureState = $gesture; Stream = $stream
      })
    }

    $validIntervals = @($pmObserved | Where-Object { $null -ne $_.IntervalMs -and $_.IntervalMs -ge 0 })
    $joined = @($pmObserved | Where-Object { $_.GestureState -ge 0 })
    $bucket120 = @($validIntervals | Where-Object { $_.IntervalMs -ge 7.5 -and $_.IntervalMs -le 9.2 })
    $bucket12To20 = @($validIntervals | Where-Object { $_.IntervalMs -ge 12.0 -and $_.IntervalMs -le 20.0 })
    $bucketIdleGap = @($validIntervals | Where-Object { $_.IntervalMs -gt 20.0 })

    # A run ends on the first non-12..20 interval in the same process/swapchain stream. Keeping streams separate
    # prevents an interleaved secondary swapchain from either joining two runs or splitting one artificially.
    $runs = New-Object System.Collections.ArrayList
    foreach ($group in @($pmObserved | Group-Object Stream)) {
      $current = New-Object System.Collections.ArrayList
      foreach ($o in @($group.Group | Sort-Object Qpc)) {
        $inRun = ($null -ne $o.IntervalMs -and $o.IntervalMs -ge 12.0 -and $o.IntervalMs -le 20.0)
        if ($inRun) { [void]$current.Add($o); continue }
        if ($current.Count -gt 0) {
          [void]$runs.Add([pscustomobject]@{
            stream = $group.Name; startQpc = [string]$current[0].Qpc; endQpc = [string]$current[$current.Count - 1].Qpc
            frameCount = $current.Count
            durationMs = [math]::Round((($current | ForEach-Object { $_.IntervalMs } | Measure-Object -Sum).Sum), 3)
            gestureActiveCount = @($current | Where-Object { $_.GestureState -gt 0 }).Count
            gestureIdleCount = @($current | Where-Object { $_.GestureState -eq 0 }).Count
            gestureUnknownCount = @($current | Where-Object { $_.GestureState -lt 0 }).Count
          })
          $current = New-Object System.Collections.ArrayList
        }
      }
      if ($current.Count -gt 0) {
        [void]$runs.Add([pscustomobject]@{
          stream = $group.Name; startQpc = [string]$current[0].Qpc; endQpc = [string]$current[$current.Count - 1].Qpc
          frameCount = $current.Count
          durationMs = [math]::Round((($current | ForEach-Object { $_.IntervalMs } | Measure-Object -Sum).Sum), 3)
          gestureActiveCount = @($current | Where-Object { $_.GestureState -gt 0 }).Count
          gestureIdleCount = @($current | Where-Object { $_.GestureState -eq 0 }).Count
          gestureUnknownCount = @($current | Where-Object { $_.GestureState -lt 0 }).Count
        })
      }
    }

    $modeHist = @($pmObserved | Where-Object { -not [string]::IsNullOrWhiteSpace("$($_.Raw.PresentMode)") } |
      Group-Object { $_.Raw.PresentMode } | Sort-Object Count -Descending | ForEach-Object {
        [ordered]@{ mode = $_.Name; count = $_.Count; pct = Pct $_.Count $pmObserved.Count }
      })
    [double[]]$runLengths = @($runs | ForEach-Object { [double]$_.frameCount })
    [double[]]$runDurations = @($runs | ForEach-Object { [double]$_.durationMs })

    $presentMonSummary = [ordered]@{
      sourceRowCount = $pmImported.Count
      selectedRowCount = $pmObserved.Count
      targetProcessId = $targetPid
      processIdFilterApplied = $pidFiltered
      applicationFallbackApplied = $applicationFallbackApplied
      timeAxis = 'PresentMon.TimeInQPC -> ScrollTrace anchor qpc/qpcFreq -> latest preceding trace state'
      gestureJoin = [ordered]@{
        joinedCount = $joined.Count
        unjoinedCount = $pmObserved.Count - $joined.Count
        coveragePct = Pct $joined.Count $pmObserved.Count
        activeCount = @($joined | Where-Object { $_.GestureState -gt 0 }).Count
        idleCount = @($joined | Where-Object { $_.GestureState -eq 0 }).Count
        tailToleranceMs = $tailToleranceMs
        reasonNotJoined = $(if ($canJoin) { 'outside the trace time range, missing TimeInQPC, or beyond the final-stamp tail tolerance' } elseif (-not $hasState) { 'scroll.csv has no state column; legacy traces cannot attest gesture state' } else { 'missing anchor/qpcFreq, trace rows, or PresentMon TimeInQPC' })
      }
      measuredIntervalCount = $validIntervals.Count
      allSelectedRowsScopeWarning = 'top-level columns include startup, idle, and every gesture state; use gestureSlices or a gesture-counted interval bucket for active-budget claims'
      presentModeHistogram = $modeHist
      presentModeSlices = @(PresentModeSliceSummary $pmObserved $pmObserved.Count)
      intervalBuckets = [ordered]@{
        panel120Like = PresentBucketSummary $bucket120 $validIntervals.Count '7.5 <= MsBetweenPresents <= 9.2'
        twelveToTwentyMs = PresentBucketSummary $bucket12To20 $validIntervals.Count '12 <= MsBetweenPresents <= 20'
        idleGap = PresentBucketSummary $bucketIdleGap $validIntervals.Count 'MsBetweenPresents > 20'
      }
      contiguousTwelveToTwentyMsRuns = [ordered]@{
        definition = 'consecutive 12..20 ms rows within one ProcessID+SwapChainAddress stream'
        runCount = $runs.Count
        totalFrames = (($runs | Measure-Object frameCount -Sum).Sum)
        lengthFrames = $(if ($runLengths.Count -gt 0) { Stats $runLengths } else { NotMeasured 'no 12..20 ms run' })
        durationMs = $(if ($runDurations.Count -gt 0) { Stats $runDurations } else { NotMeasured 'no 12..20 ms run' })
        longestRuns = @($runs | Sort-Object frameCount, durationMs -Descending | Select-Object -First 20)
      }
      gestureSlices = [ordered]@{
        active = PresentGestureSliceSummary @($pmObserved | Where-Object { $_.GestureState -gt 0 }) 'gesture state drag/ballistic/driven'
        idle = PresentGestureSliceSummary @($pmObserved | Where-Object { $_.GestureState -eq 0 }) 'gesture state idle'
        unknown = PresentGestureSliceSummary @($pmObserved | Where-Object { $_.GestureState -lt 0 }) 'gesture state unavailable/unjoined'
      }
      columns = [ordered]@{
        scope = 'all selected PresentMon rows, including startup and idle'
        msGpuBusy = PresentColumnCoverage $pmObserved 'MsGPUBusy'
        msGpuWait = PresentColumnCoverage $pmObserved 'MsGPUWait'
        msGpuTime = PresentColumnCoverage $pmObserved 'MsGPUTime'
        msRenderPresentLatency = PresentColumnCoverage $pmObserved 'MsRenderPresentLatency'
        msCpuBusy = PresentColumnCoverage $pmObserved 'MsCPUBusy'
        msCpuWait = PresentColumnCoverage $pmObserved 'MsCPUWait'
        msInPresentApi = PresentColumnCoverage $pmObserved 'MsInPresentAPI'
        msBetweenDisplayChange = PresentColumnCoverage $pmObserved 'MsBetweenDisplayChange'
        msUntilDisplayed = PresentColumnCoverage $pmObserved 'MsUntilDisplayed'
        msAllInputToPhotonLatency = PresentColumnCoverage $pmObserved 'MsAllInputToPhotonLatency'
      }
    }
  }
  catch {
    $presentMonSummary = NotMeasured "presentmon.csv could not be selected or parsed safely: $($_.Exception.Message)"
    $untrusted += "presentmon.csv could not be selected or parsed safely: $($_.Exception.Message)"
  }
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

# Discarded frames from DELTAS over scroll-active windows, not from the `coal` token. `coal` is
# publishSequence - presentedSequence, a running difference of two counters that do not track each other: a present
# that carries no new publish still advances presentedSequence, so the value can fall as well as rise and is neither
# a cumulative discarded count nor a clean instantaneous backlog. Summing per-window (publishes - presents) over the
# scroll-active lines is the quantity the bucket actually claims, and it is signed-safe because each delta pair comes
# from the same line. The raw peak is still reported, named honestly.
$publishedDuringScroll = 0; $presentedDuringScroll = 0; $coalPeak = 0; $skipTotal = 0
foreach ($r in $scrollOnly) {
  if ($r.Contains('publishDelta')) { $publishedDuringScroll += $r.publishDelta }
  if ($r.Contains('presentDelta')) { $presentedDuringScroll += $r.presentDelta }
  if ($r.Contains('coalesced')) { $coalPeak = [math]::Max($coalPeak, $r.coalesced) }
  if ($r.Contains('skipDelta')) { $skipTotal += $r.skipDelta }
}
$discarded = [math]::Max(0, $publishedDuringScroll - $presentedDuringScroll)
AddBucket 'dropOldestCoalesce' `
  'more frames were published than presented while scroll was active - the render thread replaced frames it never showed, so which one won each vblank varied' `
  'publishes and presents matched 1:1 across the scroll-active windows' `
  $discarded $null $(if ($presentedDuringScroll -le 0) { 'insufficientData' } elseif ($discarded -gt 0) { 'likelyContributor' } else { 'refuted' }) `
  "published $publishedDuringScroll vs presented $presentedDuringScroll during scroll (ratio $(if ($presentedDuringScroll) { [math]::Round($publishedDuringScroll / $presentedDuringScroll, 3) } else { 'n/a' })); raw publish-minus-present counter peaked at $coalPeak"

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

$skewFlagged = 0; $skewEvaluated = 0; $skewSamples = 0; $skewLate = 0
foreach ($p in $phaseSummaries) {
  $s = $p.cadence.clockSampleSkewMs
  if ($s -and $s.Contains('modeMs') -and $null -ne $s.modeMs) {
    $skewEvaluated++
    $skewSamples += $s.count
    $skewLate += $s.oneRefreshLateTailCount
    if ($s.modalConcentrationWithin1MsPct -lt 90.0 -or $s.oneRefreshLateTailPct -gt 5.0) { $skewFlagged++ }
  }
}
AddBucket 'clockSampling' `
  'on actually-resampled contact frames, fewer than 90% of samples concentrate within 1 ms of the mode OR more than 5% sit one-refresh-late (> mode + 0.70×the phase measured refresh period, toward zero)' `
  'the expected signed mode is concentrated and the less-negative one-refresh-late tail stays at or below 5%; the non-zero mode itself is structural resample+present latency, not a defect' `
  $skewFlagged $null `
  $(if ($skewEvaluated -eq 0) { 'insufficientData' } elseif ($skewFlagged -gt 0) { 'likelyContributor' } else { 'refuted' }) `
  "$skewFlagged of $skewEvaluated phases flagged; $skewLate of $skewSamples resampled-drag samples in the less-negative tail; selection=$(if ($hasExplicitTrackingSampleMarker) { 'i1.bit24' } else { 'legacy drag+nonblank f3' })"

# Surviving buckets, NOT ranked by taggedFrames. That sort was wrong and actively misleading: taggedFrames means
# a different thing per bucket - scrollBindThrash carries a peak binds-per-frame (22), clockSampling carries a
# count of PHASES (0-7), the stage buckets carry frame counts in the thousands - so ordering them against each
# other compared apples to oranges and put whichever bucket happened to use the largest unit on top. A reader
# would then "fix" the first entry. Each bucket carries its own evidence string; read them individually, and use
# fixOrder below for sequencing, which is causal (upstream first) rather than numeric.
$ranked = @($buckets | Where-Object { $_.verdict -eq 'likelyContributor' } | ForEach-Object { $_.name })
$noDominant = ($ranked.Count -eq 0)

# ── did the session even reproduce the complaint? ────────────────────────────────────────────────────────────
$reproduced = $null
$captureMode = $null
if ($manifest -and $manifest.PSObject.Properties['captureMode']) { $captureMode = [string]$manifest.captureMode }

$syntheticPhases = @($phaseRecords | Where-Object { $_.synthetic }).Count
$isInstrumentCheck = ($captureMode -eq 'instrumentCheck') -or ($syntheticPhases -gt 0)
if ($isInstrumentCheck) {
  $untrusted += 'instrumentCheck / unattended run: no human used the app, so this bundle validates the instrument only'
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
  # Additive schema-v1 extension: existing phase/stat keys remain intact; v2 added explicit skew provenance and the
  # PresentMon QPC join, v3 adds sequence-deduplicated whole-frame GPU execution telemetry, and v4 makes wait policy,
  # submit age, span-miss reasons, and submitted rect-area candidates first-class rather than manual console greps.
  generatorVersion = 4
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
    processId = $anchorPid
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
  # false ⇒ instrument-validation only. A free-scroll session a person ran is observed even with no 1-5 scores.
  humanObserved = ((-not $isInstrumentCheck) -and ($scored.Count -gt 0 -or $captureMode -eq 'freeScroll'))
  syntheticPhaseCount = $syntheticPhases
  phases = @($phaseSummaries)
  gpuTiming = $gpuTimingSummary
  waitPolicy = $waitPolicySummary
  renderDiagnostics = $renderDiagnosticsSummary
  presentMon = $presentMonSummary
  buckets = @($buckets)
  globalVerdict = [ordered]@{
    # Unordered on purpose - see the comment where $ranked is built. The key keeps its name for compatibility
    # with existing readers, but the list is no longer sorted and must not be read as a priority order.
    likelyContributorsUnranked = @($ranked)
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
  Step "Surviving likely contributors - UNORDERED (multi-label; read each bucket's own evidence, and use fixOrder for sequencing)"
  if ($noDominant) { Info "none - noDominantStage. The tool declines to name a suspect." }
  else { foreach ($r in $ranked) { Info $r } }
}
Write-Host ""
Step "Wrote feel-summary.json + AGENT.md into $Session"
