<#
.SYNOPSIS
  Regression fixtures for parse-scroll-csv.ps1 (the reader for legacy ScrollTrace captures).

.DESCRIPTION
  Builds tiny synthetic capture bundles under a GUID-named temporary directory and asserts the diagnostic JSON
  contracts that are easiest to regress: true JSON arrays at cardinalities 0/1/2, dead-targeting classification and
  explicit-vs-legacy tracking provenance. (The ScrollTrace-joined packer, pack-feel-summary.ps1, is retired with
  ScrollTrace: the Scroll Lab's ScrollMetrics own both pillars now - ops/diag/scroll-lab-synthetic.ps1.)

  No Pester dependency is required. The script is intentionally Windows PowerShell 5.1 compatible because the
  capture/packing entry points use that runtime on the target machine.
#>
#requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$parser = Join-Path $PSScriptRoot 'parse-scroll-csv.ps1'
if (-not (Test-Path -LiteralPath $parser)) { throw "Missing parser: $parser" }

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
