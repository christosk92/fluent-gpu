<#
.SYNOPSIS
  Regression fixtures for parse-scroll-csv.ps1 and pack-feel-summary.ps1.

.DESCRIPTION
  Builds tiny synthetic capture bundles under a GUID-named temporary directory and asserts the diagnostic JSON
  contracts that are easiest to regress: true JSON arrays at cardinalities 0/1/2, dead-targeting classification,
  explicit-vs-legacy tracking provenance, sequence-deduplicated GPU execution samples, and PresentMon PID/QPC joins.

  No Pester dependency is required. The script is intentionally Windows PowerShell 5.1 compatible because the
  capture/packing entry points use that runtime on the target machine.
#>
#requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$parser = Join-Path $PSScriptRoot 'parse-scroll-csv.ps1'
$packer = Join-Path $PSScriptRoot 'pack-feel-summary.ps1'
if (-not (Test-Path -LiteralPath $parser)) { throw "Missing parser: $parser" }
if (-not (Test-Path -LiteralPath $packer)) { throw "Missing packer: $packer" }

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$runId = [Guid]::NewGuid().ToString('N')
$expectedLeaf = "fluentgpu-diag-tests-$runId"
$testRoot = Join-Path ([IO.Path]::GetTempPath()) $expectedLeaf
$passed = 0
$failures = New-Object System.Collections.ArrayList

function Write-LinesNoBom([string]$Path, [string[]]$Lines) {
  [IO.File]::WriteAllLines($Path, $Lines, $script:utf8NoBom)
}

function Write-TextNoBom([string]$Path, [string]$Text) {
  [IO.File]::WriteAllText($Path, $Text, $script:utf8NoBom)
}

function New-CaseDirectory([string]$Name) {
  $safeName = $Name -replace '[^A-Za-z0-9_.-]', '_'
  $path = Join-Path $script:testRoot $safeName
  [void](New-Item -ItemType Directory -Path $path)
  return $path
}

function New-TraceRow {
  param(
    [string]$TMs,
    [string]$Kind,
    [string]$Frame = '1',
    [string]$I0 = '',
    [string]$I1 = '',
    [string]$I2 = '',
    [string]$F0 = '',
    [string]$F1 = '',
    [string]$F2 = '',
    [string]$F3 = '',
    [string]$F4 = '',
    [string]$F5 = '',
    [string]$AuxMs = '',
    [string]$State = ''
  )
  return [pscustomobject][ordered]@{
    tMs = $TMs; frame = $Frame; kind = $Kind
    i0 = $I0; i1 = $I1; i2 = $I2
    f0 = $F0; f1 = $F1; f2 = $F2; f3 = $F3; f4 = $F4; f5 = $F5
    auxMs = $AuxMs; state = $State
  }
}

function Write-TraceCsv([string]$Path, [object[]]$Rows, [switch]$Legacy) {
  if ($Rows.Count -eq 0) { throw 'A parser fixture needs at least one CSV data row.' }
  if ($Legacy) {
    $projected = @($Rows | Select-Object tMs, frame, kind, i0, i1, i2, f0, f1, f2, f3, f4, f5, auxMs)
  }
  else {
    $projected = @($Rows | Select-Object tMs, frame, kind, i0, i1, i2, f0, f1, f2, f3, f4, f5, auxMs, state)
  }
  Write-LinesNoBom $Path ([string[]]@($projected | ConvertTo-Csv -NoTypeInformation))
}

function Read-Json([string]$Path) {
  return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
}

function Assert-True([bool]$Condition, [string]$Message) {
  if (-not $Condition) { throw $Message }
}

function Assert-Equal($Expected, $Actual, [string]$Message) {
  if ($Expected -ne $Actual) {
    throw "$Message (expected '$Expected', actual '$Actual')"
  }
}

function Assert-JsonArray($Value, [int]$ExpectedCount, [string]$Message) {
  if (-not ($Value -is [Array])) {
    $typeName = if ($null -eq $Value) { '<null>' } else { $Value.GetType().FullName }
    throw "$Message must be a JSON array after ConvertFrom-Json, got $typeName"
  }
  Assert-Equal $ExpectedCount $Value.Count "$Message cardinality"
}

function Invoke-ParserFixture([string]$Name, [object[]]$Rows, [switch]$Legacy) {
  $dir = New-CaseDirectory $Name
  $csv = Join-Path $dir 'scroll.csv'
  $json = Join-Path $dir 'summary.json'
  Write-TraceCsv $csv $Rows -Legacy:$Legacy
  & $script:parser -Csv $csv -Json $json 3>$null 4>$null 6>$null | Out-Null
  return Read-Json $json
}

function Write-MinimalManifest([string]$Session, [bool]$DiagBuild) {
  $diag = if ($DiagBuild) { 'true' } else { 'false' }
  Write-TextNoBom (Join-Path $Session 'manifest.json') `
    "{`"build`":{`"fluentGpuDiag`":$diag,`"gitDirty`":false},`"power`":{`"idleCpuPctPreCapture`":0},`"switches`":{},`"effectiveKnobs`":{},`"env`":{}}"
}

function Invoke-PackerFixture([string]$Name, [object[]]$Rows, [string[]]$ConsoleLines,
                              [string[]]$PresentMonLines, [switch]$Legacy, [bool]$DiagBuild = $true) {
  $session = New-CaseDirectory $Name
  Write-TraceCsv (Join-Path $session 'scroll.csv') $Rows -Legacy:$Legacy
  Write-LinesNoBom (Join-Path $session 'console.txt') $ConsoleLines
  Write-MinimalManifest $session $DiagBuild
  if ($null -ne $PresentMonLines) {
    Write-LinesNoBom (Join-Path $session 'presentmon.csv') $PresentMonLines
  }
  & $script:packer -Session $session 3>$null 4>$null 6>$null | Out-Null
  return Read-Json (Join-Path $session 'feel-summary.json')
}

function Test-Case([string]$Name, [scriptblock]$Body) {
  try {
    & $Body
    $script:passed++
    Write-Host "PASS $Name" -ForegroundColor Green
  }
  catch {
    [void]$script:failures.Add("${Name}: $($_.Exception.Message)")
    Write-Host "FAIL $Name - $($_.Exception.Message)" -ForegroundColor Red
  }
}

try {
  [void](New-Item -ItemType Directory -Path $testRoot)

  foreach ($count in 0, 1, 2) {
    $capturedCount = $count
    Test-Case "parser arrays keep cardinality $capturedCount" {
      $rows = New-Object System.Collections.ArrayList
      [void]$rows.Add((New-TraceRow -TMs '0' -Kind 'frame'))
      for ($i = 0; $i -lt $capturedCount; $i++) {
        # note105: reason 1 + hit-context bit. Reason 1 is deliberate wheel-handler ownership, not a dead candidate.
        [void]$rows.Add((New-TraceRow -TMs "$(1 + $i).1" -Kind 'note' -I0 '105' -I1 '65' -I2 "$(100 + $i)" -F2 '11' -F3 '22'))
        [void]$rows.Add((New-TraceRow -TMs "$(1 + $i).2" -Kind 'note' -I0 '106' -I1 "$(200 + $i)" -I2 '0' -F0 '3' -F1 '4'))
        # wheelSeed: dropped + class 1 (NoScroller) + hit-context bit = 0x430.
        [void]$rows.Add((New-TraceRow -TMs "$(1 + $i).3" -Kind 'wheelSeed' -I0 "$(300 + $i)" -I1 '1072' -F3 '33' -F4 '44'))
      }
      $result = Invoke-ParserFixture "arrays-$capturedCount" @($rows)
      Assert-JsonArray $result.targeting.note105Observations $capturedCount 'note105Observations'
      Assert-JsonArray $result.targeting.note106Observations $capturedCount 'note106Observations'
      Assert-JsonArray $result.targeting.wheelNoScrollerObservations $capturedCount 'wheelNoScrollerObservations'
    }
  }

  Test-Case 'parser distinguishes owned, recovered, and unresolved targeting refusals' {
    $rows = @(
      New-TraceRow -TMs '0' -Kind 'frame'
      # Reason 1 is element-wheel ownership. A later end phase must not turn it into a dead-targeting candidate.
      New-TraceRow -TMs '1' -Kind 'note' -I0 '105' -I1 '1'
      New-TraceRow -TMs '2' -Kind 'phase' -I0 '12'
      # Retryable reason 2, then wheel fallback proves class-4 ElementHandled.
      New-TraceRow -TMs '3' -Kind 'note' -I0 '105' -I1 '2'
      # 0x890 = dropped + class 4 + phase-fallback provenance. A generic element-handled wheel from another
      # interaction must not close this refusal.
      New-TraceRow -TMs '4' -Kind 'wheelSeed' -I1 '2192'
      # Retryable reason 2 remains unresolved at EOF and is the sole dead candidate.
      New-TraceRow -TMs '5' -Kind 'note' -I0 '105' -I1 '2'
    )
    $result = Invoke-ParserFixture 'targeting-classes' $rows
    Assert-Equal 3 $result.targeting.note105LatchRefused 'all refusals counted'
    Assert-Equal 1 $result.targeting.note105ReasonWheelHandlerFallback 'reason 1 count'
    Assert-Equal 2 $result.targeting.note105ReasonNoScrollerEitherAxis 'reason 2 count'
    Assert-Equal 2 $result.targeting.retryableRefusals 'retryable reason 2 count'
    Assert-Equal 1 $result.targeting.retryableRefusalWheelHandledBeforeEnd 'later class 4 recovery count'
    Assert-Equal 0 $result.targeting.retryableRefusalRelatchedBeforeEnd 'no note 106 recovery count'
    Assert-Equal 1 $result.targeting.deadTargetingCandidates 'only unresolved reason 2 is dead'
  }

  Test-Case 'parser preserves explicit bit24 exact-zero tracking samples' {
    $rows = @(
      # Explicit validity is authoritative even when aggregate state says idle and compact CSV encodes exact zero blank.
      New-TraceRow -TMs '1' -Kind 'latency' -I0 '1' -I1 '16777219' -F3 '' -State '1'
      # Once any explicit marker exists, an unmarked legacy-looking value is not silently mixed into the distribution.
      New-TraceRow -TMs '2' -Kind 'latency' -I0 '2' -I1 '3' -F3 '-99' -State '17'
    )
    $result = Invoke-ParserFixture 'tracking-explicit-zero' $rows
    Assert-Equal 'explicit-bit24' $result.latency.trackingValidity 'explicit tracking provenance'
    Assert-Equal 1 $result.latency.trackingRows 'only marked row selected'
    Assert-Equal 1 $result.latency.clockSampleSkewMs.count 'exact-zero sample remains measured'
    Assert-Equal 0 $result.latency.clockSampleSkewMs.p50 'blank marked f3 decodes as exact zero'
  }

  Test-Case 'parser legacy fallback requires drag state and nonblank skew' {
    $rows = @(
      New-TraceRow -TMs '1' -Kind 'latency' -I0 '1' -I1 '3' -F3 '' -State '17'
      New-TraceRow -TMs '2' -Kind 'latency' -I0 '2' -I1 '3' -F3 '-20' -State '17'
      New-TraceRow -TMs '3' -Kind 'latency' -I0 '3' -I1 '3' -F3 '-30' -State '1'
    )
    $result = Invoke-ParserFixture 'tracking-legacy-selection' $rows
    Assert-Equal 'legacy-drag-and-nonempty-f3' $result.latency.trackingValidity 'legacy tracking provenance'
    Assert-Equal 1 $result.latency.trackingRows 'blank drag and non-drag value excluded'
    Assert-Equal -20 $result.latency.clockSampleSkewMs.p50 'only nonblank drag value selected'
  }

  Test-Case 'packer deduplicates gexec and joins only matching PID/QPC rows' {
    $rows = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt 20; $i++) {
      # state 17 = phase 1 + drag. i1 bit24 + hardware quality; blank f3 is an explicit exact-zero sample.
      [void]$rows.Add((New-TraceRow -TMs "$i" -Frame "$(1 + $i)" -Kind 'latency' -I0 "$(1 + $i)" `
        -I1 '16777219' -I2 '0' -F0 '' -F1 '' -F2 '-4' -F3 '' -F4 '8.333' -F5 '0.5' -State '17'))
    }
    $console = @(
      '[scrolltrace] anchor qpc=1000 qpcFreq=1000 wallUtc=2026-08-16T12:00:00.0000000Z trace=1 pid=42'
      '[fps] tMs=1 scroll loop 120fps 1.0ms presentD=1 pubD=1 coal=0 lag=0 skipD=0 gpu 6.0ms latW0.0 gexec 4.0ms#10 gexecAge=2 rq1/9 rareaMp=1/5 rareaSeq=20 btop=7:4.5:0.2:100x200:5 smiss=gd0/sb0/ed0/ek3/ec0/cap0/mg1/mk0/geo0/mc0/mp0 wait display8 100x100@120Hz'
      '[fps] tMs=2 scroll loop 120fps 1.0ms presentD=1 pubD=1 coal=0 lag=0 skipD=0 gpu 6.0ms latW0.0 gexec 4.0ms#10 gexecAge=3 rq2/18 rareaMp=2/6 rareaSeq=21 btop=8:5.0:0.2:100x200:5 smiss=gd0/sb0/ed0/ek2/ec0/cap0/mg0/mk0/geo0/mc0/mp0 wait display8 100x100@120Hz'
      '[fps] tMs=3 loop 120fps 1.0ms presentD=1 pubD=1 coal=0 lag=0 skipD=0 gpu 6.0ms latW0.0 gexec 6.0ms#12 gexecAge=2 wait adaptive-gpu33 100x100@120Hz'
      '[fps] tMs=4 scroll loop 120fps 1.0ms presentD=1 pubD=1 coal=0 lag=0 skipD=0 gpu 6.0ms latW0.0 gexec 6.0ms#12 gexecAge=3 rq2/18 rareaMp=2/6 rareaSeq=21 btop=8:5.0:0.2:100x200:5 wait display8 100x100@120Hz'
      '[fps] tMs=5 scroll loop 120fps 1.0ms presentD=1 pubD=1 coal=0 lag=0 skipD=0 gpu 6.0ms latW0.0 gexec 8.0ms#14 gexecAge=7 wait display8 100x100@120Hz'
    )
    $presentMon = @(
      'ProcessID,SwapChainAddress,TimeInQPC,MsBetweenPresents,PresentMode,MsGPUBusy'
      '41,0xBAD,1000,99,Composed: Flip,90'
      '42,0xAAA,999,8.3,Composed: Flip,4.0'
      '42,0xAAA,1000,8.3,Composed: Flip,4.1'
      '42,0xAAA,1010,8.3,Hardware Composed: Independent Flip,4.2'
      '42,0xAAA,1119,8.3,Composed: Flip,4.3'
      '42,0xAAA,1120,8.3,Hardware Composed: Independent Flip,4.4'
      '42,0xAAA,,8.3,Composed: Flip,4.5'
    )
    $result = Invoke-PackerFixture 'packer-gexec-presentmon' @($rows) $console $presentMon
    $gexec = $result.gpuTiming.gpuExecutionMs
    Assert-Equal 5 $gexec.rawTokenCount 'raw gexec tokens'
    Assert-Equal 3 $gexec.uniqueSampleCount 'unique gexec sequences'
    Assert-Equal 2 $gexec.staleRepeatTokenCount 'stale gexec repetitions'
    Assert-Equal 0 $gexec.tokenWithoutSubmitAgeCount 'current gexec tokens carry submit age'
    Assert-Equal 0 $gexec.submitAgeRegressionTokenCount 'repeated sample age is monotonic'
    Assert-Equal 2 $gexec.unobservedSequenceGapCount 'unobserved sequence gaps'
    Assert-Equal 3 $gexec.allUniqueSamplesMs.count 'deduplicated distribution count'
    Assert-Equal 6 $gexec.allUniqueSamplesMs.mean 'deduplicated distribution mean'
    Assert-Equal 1 $gexec.scrollFirstObservedTooOldCount 'old first-observed sample excluded from scroll attribution'
    Assert-Equal 1 $gexec.scrollFirstObservedUniqueSamplesMs.count `
      "first-observed scroll attribution: $($gexec.scrollFirstObservedUniqueSamplesMs | ConvertTo-Json -Compress)"
    Assert-Equal 4 $gexec.scrollFirstObservedUniqueSamplesMs.p50 'idle-first sequence is not reassigned to scroll'

    Assert-Equal 1 $result.waitPolicy.allFps.adaptiveGpuLineCount 'adaptive wait is surfaced in all-line policy summary'
    Assert-Equal 0 $result.waitPolicy.scrollActiveFps.adaptiveGpuLineCount 'fixture adaptive wait remains outside scroll'
    $span = $result.renderDiagnostics.spanReuseMisses.scrollActiveFps
    Assert-Equal 2 $span.observedLineCount 'scroll smiss rows parsed'
    Assert-Equal 5 $span.reasons.exactKey.sumAcrossObservedFrames 'span exact-key misses summed'
    Assert-Equal 1 $span.reasons.moveGuard.sumAcrossObservedFrames 'span move-guard misses summed'
    $area = $result.renderDiagnostics.submittedRectArea.scrollActiveFps
    Assert-Equal 3 $area.observedLineCount 'raw repeated rect-snapshot lines remain visible as coverage'
    Assert-Equal 2 $area.summarizedSnapshotCount 'rareaSeq removes a repeated backend snapshot from statistics'
    Assert-Equal 1 $area.deduplication.repeatedOrFirstObservedOutsideScopeCount 'one repeated rareaSeq observation removed'
    Assert-Equal 5.5 $area.blendedSubmittedMp.mean 'blended submitted area summarized'
    Assert-Equal 1.5 $area.opaqueInstances.mean 'opaque rq instances summarized on unique snapshots'
    Assert-Equal 13.5 $area.blendedInstances.mean 'blended rq instances summarized on unique snapshots'
    Assert-Equal 0.9 $area.blendedInstanceFraction.mean 'rq blended fraction uses unique snapshots'
    Assert-Equal 1 @($area.repeatedTopCandidates).Count 'repeated blended candidate grouped by geometry/alpha/flags'
    Assert-Equal 2 @($area.repeatedTopCandidates)[0].observationCount 'repeated blended candidate observation count'

    $phase = @($result.phases)[0]
    Assert-Equal 20 $phase.latency.trackingSampleFrames 'packer selects all explicit exact-zero samples'
    Assert-Equal 20 $phase.cadence.clockSampleSkewMs.count 'packer publishes explicit exact-zero skew distribution'
    Assert-Equal 0 $phase.cadence.clockSampleSkewMs.p50 'packer preserves explicit exact zero'
    Assert-Equal 5.833 $phase.cadence.clockSampleSkewMs.oneRefreshLateThresholdOffsetMs `
      'one-refresh-late split scales from the measured 8.333ms refresh period'

    Assert-Equal 7 $result.presentMon.sourceRowCount 'PresentMon source rows'
    Assert-Equal 6 $result.presentMon.selectedRowCount 'PID filter removes foreign process'
    Assert-True ([bool]$result.presentMon.processIdFilterApplied) 'PID filter must be reported as applied'
    Assert-Equal 42 $result.presentMon.targetProcessId 'anchor PID is authoritative'
    Assert-Equal 3 $result.presentMon.gestureJoin.joinedCount 'exact start, interior, and inclusive tail edge join'
    Assert-Equal 3 $result.presentMon.gestureJoin.unjoinedCount 'pre-trace, past-tail, and missing QPC remain unknown'
    Assert-Equal 3 $result.presentMon.gestureJoin.activeCount 'joined rows inherit drag state'
    Assert-Equal 0 $result.presentMon.gestureJoin.idleCount 'unknown rows are never coerced to idle'
    Assert-JsonArray $result.presentMon.presentModeSlices 2 'presentModeSlices'
    $independent = @($result.presentMon.presentModeSlices | Where-Object { $_.mode -eq 'Hardware Composed: Independent Flip' })[0]
    Assert-Equal 2 $independent.rowCount 'independent-flip rows stay in their own mode slice'
    Assert-Equal 1 $independent.gestureSlices.active.rowCount 'mode slice retains active rows separately'
    Assert-Equal 1 $independent.gestureSlices.unknown.rowCount 'mode slice retains unjoined rows separately'
  }

  Test-Case 'packer refuses gesture joins for legacy state-less traces' {
    $rows = @(
      New-TraceRow -TMs '0' -Kind 'latency' -I0 '1' -I1 '3' -F3 '-20' -F4 '8.333'
    )
    $console = @(
      '[scrolltrace] anchor qpc=1000 qpcFreq=1000 wallUtc=2026-08-16T12:00:00.0000000Z trace=1 pid=42'
      '[fps] tMs=1 scroll loop 120fps 1.0ms presentD=1 pubD=1 coal=0 lag=0 skipD=0 gpu 6.0ms latW0.0 rq1/118 wait display8 100x100@120Hz'
    )
    $presentMon = @(
      'ProcessID,SwapChainAddress,TimeInQPC,MsBetweenPresents,PresentMode'
      '42,0xAAA,1000,8.3,Composed: Flip'
    )
    $result = Invoke-PackerFixture 'packer-legacy-no-state' $rows $console $presentMon -Legacy
    Assert-Equal 1 $result.presentMon.selectedRowCount 'legacy PresentMon row remains selected'
    Assert-Equal 0 $result.presentMon.gestureJoin.joinedCount 'state-less trace cannot attest a gesture'
    Assert-Equal 1 $result.presentMon.gestureJoin.unjoinedCount 'state-less row remains unknown'
    Assert-Equal 0 $result.presentMon.gestureJoin.idleCount 'state-less row is not manufactured idle'
    Assert-True ($result.presentMon.gestureJoin.reasonNotJoined -like '*no state column*') `
      'join refusal must name the missing legacy state column'
    Assert-JsonArray $result.presentMon.presentModeSlices 1 'one-mode presentModeSlices'
    $legacyRect = $result.renderDiagnostics.submittedRectArea.scrollActiveFps
    Assert-True ($legacyRect.deduplication.mode -like 'legacy unsequenced*') `
      'legacy rq lines without rareaSeq must label that stale repeats cannot be removed'
    Assert-Equal 118 $legacyRect.blendedInstances.p50 'legacy rq fallback remains readable'
    $unmeasuredSpanSum = $result.renderDiagnostics.spanReuseMisses.scrollActiveFps.reasons.exactKey.sumAcrossObservedFrames
    Assert-True ($null -ne $unmeasuredSpanSum.reasonNotMeasured) `
      'an absent smiss census must be NotMeasured rather than a numeric zero'
  }

  Test-Case 'packer rejects malformed nonblank trace numerics' {
    $rows = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt 20; $i++) {
      $overrun = if ($i -eq 0) { 'not-a-number' } else { '-2' }
      [void]$rows.Add((New-TraceRow -TMs "$i" -Kind 'latency' -I0 "$($i + 1)" -I1 '16777219' `
        -F2 $overrun -F3 '-20' -F4 '8.333' -State '17'))
    }
    $console = @(
      '[scrolltrace] anchor qpc=1000 qpcFreq=1000 wallUtc=2026-08-16T12:00:00.0000000Z trace=1 pid=42'
      '[fps] tMs=1 scroll loop 120fps 1.0ms presentD=1 pubD=1 coal=0 lag=0 skipD=0 gpu 6.0ms latW0.0 wait display8 100x100@120Hz'
    )
    $thrown = $null
    try { [void](Invoke-PackerFixture 'packer-malformed-number' @($rows) $console $null) }
    catch { $thrown = $_ }
    Assert-True ($null -ne $thrown) 'malformed nonblank trace numeric must stop the packer'
    Assert-True ($thrown.Exception.Message -like "*Invalid invariant-culture number 'not-a-number'*") `
      'malformed numeric failure must name the rejected value'
  }

  Test-Case 'packer uses only an unambiguous Wavee application fallback when PID is absent' {
    $rows = @(New-TraceRow -TMs '0' -Kind 'latency' -I0 '1' -I1 '16777219' -F3 '-20' -F4 '8.333' -State '17')
    $console = @(
      '[scrolltrace] anchor qpc=1000 qpcFreq=1000 wallUtc=2026-08-16T12:00:00.0000000Z trace=1'
      '[fps] tMs=1 scroll loop 120fps 1.0ms presentD=1 pubD=1 coal=0 lag=0 skipD=0 gpu 6.0ms latW0.0 wait display8 100x100@120Hz'
    )
    $presentMon = @(
      'Application,ProcessID,SwapChainAddress,TimeInQPC,MsBetweenPresents,PresentMode'
      'Other.exe,88,0xBAD,1000,99,Composed: Flip'
      'Wavee.exe,77,0xAAA,1000,8.3,Composed: Flip'
    )
    $result = Invoke-PackerFixture 'packer-application-fallback' $rows $console $presentMon
    Assert-Equal 1 $result.presentMon.selectedRowCount 'only Wavee application population selected'
    Assert-Equal 77 $result.presentMon.targetProcessId 'unique Wavee ProcessID recovered'
    Assert-True ([bool]$result.presentMon.applicationFallbackApplied) 'application fallback must be explicit'
  }

  Test-Case 'packer preserves an empty PresentMode slice as a JSON array' {
    $rows = @(New-TraceRow -TMs '0' -Kind 'latency' -I0 '1' -I1 '16777219' -F3 '-20' -F4 '8.333' -State '17')
    $console = @(
      '[scrolltrace] anchor qpc=1000 qpcFreq=1000 wallUtc=2026-08-16T12:00:00.0000000Z trace=1 pid=42'
      '[fps] tMs=1 scroll loop 120fps 1.0ms presentD=1 pubD=1 coal=0 lag=0 skipD=0 gpu 6.0ms latW0.0 wait display8 100x100@120Hz'
    )
    $presentMon = @(
      'ProcessID,SwapChainAddress,TimeInQPC,MsBetweenPresents,PresentMode'
      '42,0xAAA,1000,8.3,'
    )
    $result = Invoke-PackerFixture 'packer-empty-present-mode' $rows $console $presentMon
    Assert-JsonArray $result.presentMon.presentModeSlices 0 'zero-mode presentModeSlices'
  }

  Test-Case 'packer refuses ambiguous Wavee application populations when PID is absent' {
    $rows = @(New-TraceRow -TMs '0' -Kind 'latency' -I0 '1' -I1 '16777219' -F3 '-20' -F4 '8.333' -State '17')
    $console = @(
      '[scrolltrace] anchor qpc=1000 qpcFreq=1000 wallUtc=2026-08-16T12:00:00.0000000Z trace=1'
      '[fps] tMs=1 scroll loop 120fps 1.0ms presentD=1 pubD=1 coal=0 lag=0 skipD=0 gpu 6.0ms latW0.0 wait display8 100x100@120Hz'
    )
    $presentMon = @(
      'Application,ProcessID,SwapChainAddress,TimeInQPC,MsBetweenPresents,PresentMode'
      'Wavee.exe,77,0xAAA,1000,8.3,Composed: Flip'
      'Wavee.exe,78,0xBBB,1001,8.3,Composed: Flip'
    )
    $result = Invoke-PackerFixture 'packer-application-ambiguous' $rows $console $presentMon
    Assert-True ($result.presentMon.reasonNotMeasured -like '*span 2 process IDs*') `
      'ambiguous Wavee processes must make PresentMon not measured'
  }

  if ($failures.Count -gt 0) {
    throw ("{0} diagnostic regression fixture(s) failed:`n - {1}" -f $failures.Count, ($failures -join "`n - "))
  }
  Write-Host "ALL DIAGNOSTIC FIXTURES PASSED ($passed cases)" -ForegroundColor Green
}
finally {
  if (Test-Path -LiteralPath $testRoot) {
    $resolved = (Resolve-Path -LiteralPath $testRoot).Path
    $tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $tempBase.EndsWith([IO.Path]::DirectorySeparatorChar.ToString())) {
      $tempBase += [IO.Path]::DirectorySeparatorChar
    }
    $leaf = Split-Path -Leaf $resolved
    $insideTemp = $resolved.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase)
    if (-not $insideTemp -or $leaf -cne $expectedLeaf -or $leaf -notmatch '^fluentgpu-diag-tests-[0-9a-f]{32}$') {
      throw "Refusing unsafe fixture cleanup target: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
  }
}
