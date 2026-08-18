<#
.SYNOPSIS
  Run ONE free-scroll Wavee capture and emit a self-describing session bundle.

.DESCRIPTION
  Launch Wavee with the feel-instrument environment, let the operator scroll however they want, then pack
  console.txt + scroll.csv when they close the window. No gesture script, no ENTER-when-ready, no 1-5 ratings.

  "Smooth scroll" is two independent properties, and they trade against each other:

    Pillar A - glued:  does the content sit where the finger is?     (input -> offset commit)
    Pillar B - steady: are submit-confirmed presents evenly paced?   (offset -> record -> publish -> present)

  A pacing queue improves B and worsens A. The packager keeps them structurally separate. Do not invent a fused
  smoothness score, and do not ask a human to rate either pillar - the traces already contain both.

  What this script produces (ops/diag/sessions/<utcStamp>-<sha>/):
    manifest.json   build + machine + display + power + the full env dump, each var tagged default/overridden/cleared
    console.txt     stdout AND stderr, merged; MUST contain the [scrolltrace] anchor line
    scroll.csv      the ScrollTrace POD ring (diag builds only)
    phases.jsonl    one freeScroll slice covering the whole session (wall clock + QPC; scores are always null)
    feel-summary.json / AGENT.md   written by pack-feel-summary.ps1 (run automatically at the end)

.EXAMPLE
  ops\diag\wavee-scroll-session.cmd -Diag
  powershell -File ops\diag\wavee-scroll-session.ps1 -SkipPublish -ExePath C:\path\Wavee.exe

.NOTES
  Windows PowerShell 5.1 ONLY: no && / ||, no ternary, no ?? / ?., no -AsHashtable, no ConvertFrom-Json -Depth.
  ConvertTo-Json defaults to -Depth 2 and silently renders deeper nodes as type names, so every write here passes
  -Depth 12 and goes out BOM-free (-Encoding utf8 writes a BOM on 5.1, which breaks naive readers).
#>
#requires -Version 5.1
[CmdletBinding()]
param(
  # Machine architecture from the ENVIRONMENT, not from RuntimeInformation.OSArchitecture: under Windows PowerShell
  # 5.1 (.NET Framework) an x64-emulated host on an ARM64 machine reports X64 for the OS, so a session launched from
  # an emulated shell silently looked for a win-x64 publish that does not exist. PROCESSOR_ARCHITEW6432 is set only
  # inside an emulated/WOW process and always names the REAL machine, so it takes precedence.
  [ValidateSet('arm64', 'x64')]
  [string]$Arch = $(
    $a = $env:PROCESSOR_ARCHITEW6432
    if (-not $a) { $a = $env:PROCESSOR_ARCHITECTURE }
    if ("$a" -match 'ARM64') { 'arm64' } else { 'x64' }),
  # Build WITH FLUENTGPU_DIAG. Without it there is no scroll.csv, no [renderbudget], and no -Opaque A/B - the
  # console streams still work, but pillar A has no data at all.
  [switch]$Diag,
  # A/B arm: replace the DWM Mica composition path with an opaque HWND swapchain. Compile-fenced, so it REQUIRES -Diag.
  [switch]$Opaque,
  # Tier 2. Costs up to 256 extra EndQuery per frame plus a fixed 259-slot resolve EVERY frame, and the boundary
  # count PEAKS on exactly the dense fill/image/glyph list being flung. Off unless the GPU is already implicated.
  [switch]$GpuTiming,
  [switch]$PresentInterval0,
  # BISECTION ARM. Suppresses the phase-7.5 image pump while scroll is active. This is the only thing that can
  # settle the imageDecodeDuringScroll bucket: its predicate is a correlation, and its refuter is defined as
  # "the identical phase with the pump disabled shows the same cadence". Run it as a SECOND session against a
  # first one captured with identical switches - one bisection is worth more than five more metrics, because a
  # bisection yields a causal claim and metrics yield correlations.
  [switch]$NoImagePump,
  [switch]$SkipPublish,
  [string]$ExePath,
  [string]$OutRoot,
  # Skip the packaging step (leave the raw bundle for manual inspection).
  [switch]$NoPack,
  # UNATTENDED: launch, idle briefly, close. Stamped instrumentCheck / synthetic. Validates the toolchain
  # (diag build armed, anchor landed, streams merged, packager ran). Nobody scrolled, so it cannot answer
  # a feel question. Never report a scroll conclusion from one.
  [switch]$Unattended,
  [int]$UnattendedSeconds = 4,
  # Proceed even though the machine is not idle. The measured value is recorded either way; this only skips the
  # refusal, and the bundle is stamped untrusted so the override cannot be forgotten later.
  [switch]$AllowBusyMachine
)
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Info($m) { Write-Host "    $m" -ForegroundColor DarkGray }
function Warn($m) { Write-Host "    $m" -ForegroundColor Yellow }
function Say($m)  { Write-Host $m -ForegroundColor White }

# The app polls the phase marker while we write it. The app opens it share-ReadWrite so it cannot lock us out, but
# a write can still lose a race with an antivirus scan or an indexer, and losing a phase marker mid-session would
# silently mis-attribute every subsequent row. Retry briefly rather than abort a capture the operator is standing
# in front of.
function WriteMarker($path, $text) {
  for ($i = 0; $i -lt 20; $i++) {
    try { [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.ASCIIEncoding)); return }
    catch { Start-Sleep -Milliseconds 25 }
  }
  throw "Could not write the phase marker after 20 attempts: $path"
}

function WriteJsonNoBom($obj, $path) {
  $text = $obj | ConvertTo-Json -Depth 12
  [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

# ── switch validation: refuse mislabelled runs rather than produce them ───────────────────────────────────────
# A bundle that SAYS opaque but ran Mica is worse than no bundle: it looks like evidence and it is not.
if ($Opaque -and -not $Diag) {
  throw "-Opaque requires -Diag. FG_OPAQUE_WINDOW is compiled behind '#if DEBUG || FLUENTGPU_DIAG', so a plain Release run would silently stay on Mica while the manifest claimed otherwise."
}
if ($PresentInterval0 -and -not $GpuTiming) {
  throw "-PresentInterval0 requires -GpuTiming. The interactive-present path is gated on gpuRenderMs > 0, which only FG_GPU_TIMING produces, so without it the switch is a no-op that would still be recorded as an arm."
}
if ($NoImagePump -and -not $Diag) {
  throw "-NoImagePump requires -Diag. The arm is compiled behind '#if DEBUG || FLUENTGPU_DIAG', so a plain Release run would pump images normally while the manifest claimed the bisection had been performed - which would turn 'no change' into a false refutation."
}

# ── build identity ───────────────────────────────────────────────────────────────────────────────────────────
Step "Build identity"
Push-Location $root
try {
  $gitSha = (& git rev-parse HEAD 2>$null)
  $gitBranch = (& git rev-parse --abbrev-ref HEAD 2>$null)
  $gitDirty = ((& git status --porcelain 2>$null) | Measure-Object).Count -gt 0
}
finally { Pop-Location }
if (-not $gitSha) { $gitSha = 'unknown'; $gitBranch = 'unknown'; $gitDirty = $true }
$shortSha = if ($gitSha.Length -ge 8) { $gitSha.Substring(0, 8) } else { $gitSha }
Info "sha $shortSha  branch $gitBranch  dirty $gitDirty"
if ($gitDirty) { Warn "Working tree is DIRTY. The bundle records this; a dirty capture is not reproducible from the sha alone." }

# ── publish ──────────────────────────────────────────────────────────────────────────────────────────────────
$publishArgs = @()
if (-not $SkipPublish) {
  Step "Publishing Wavee ($Arch$(if ($Diag) { ', FLUENTGPU_DIAG' }))"
  # Named arguments, not an array splat: `& script @('-Arch', $Arch)` binds the
  # literal "-Arch" to the ValidateSet parameter and fails. bench-wavee.ps1 uses this form.
  $publishArgs = @('-Arch', $Arch)
  if ($Diag) { $publishArgs += '-Diag' }
  if ($Diag) {
    & (Join-Path $root 'ops\build\publish-wavee-aot.ps1') -Arch $Arch -Diag
  } else {
    & (Join-Path $root 'ops\build\publish-wavee-aot.ps1') -Arch $Arch
  }
  if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }
}
if (-not $ExePath) {
  if ($Diag) { $ExePath = Join-Path $root "src\apps\Wavee\bin\publish-aot-diag\win-$Arch\Wavee.exe" }
  else { $ExePath = Join-Path $root "src\apps\Wavee\bin\Release\net10.0\win-$Arch\publish\Wavee.exe" }
}
if (-not (Test-Path $ExePath)) { throw "Wavee.exe not found: $ExePath (publish first, or pass -ExePath)" }
$exeInfo = Get-Item $ExePath
$exeSha = (Get-FileHash -Path $ExePath -Algorithm SHA256).Hash
Info "exe $ExePath"
Info "sha256 $exeSha  $([math]::Round($exeInfo.Length / 1MB, 2)) MB"

# ── preflight: refuse to measure a machine that is already busy ───────────────────────────────────────────────
# Microsoft's own first step for any latency measurement. A capture taken while something else is eating the CPU
# produces hitches that belong to that other thing, and nothing in the bundle can tell them apart afterwards.
Step "Preflight"
# Wait for the machine to settle BEFORE measuring, whatever made it busy. A NativeAOT publish saturates every core
# and leaves a tail of compiler/MSBuild teardown for tens of seconds - but so does an editor indexing, a sync client,
# or a build someone kicked off elsewhere. Gating this on -SkipPublish was wrong: it assumed the only possible cause
# was our own publish, so a session started right after ANY heavy work refused instead of waiting a few seconds.
Info "Waiting for the machine to settle (up to 60 s)..."
for ($w = 0; $w -lt 30; $w++) {
  try { $c = (Get-Counter '\Processor(_Total)\% Processor Time' -ErrorAction Stop).CounterSamples[0].CookedValue }
  catch { break }
  if ($c -lt 5.0) { break }
  Start-Sleep -Seconds 2
}
$idleCpu = 0.0
try {
  $samples = @()
  for ($i = 0; $i -lt 3; $i++) {
    $samples += (Get-Counter '\Processor(_Total)\% Processor Time' -ErrorAction Stop).CounterSamples[0].CookedValue
    Start-Sleep -Milliseconds 400
  }
  $idleCpu = [math]::Round((($samples | Measure-Object -Average).Average), 1)
}
catch { $idleCpu = -1 }
if ($idleCpu -lt 0) { Warn "Could not sample idle CPU (perf counters unavailable). Recording -1; treat absolute ms as suspect." }
else {
  Info "idle CPU $idleCpu%"
  if ($idleCpu -gt 5.0) {
    Warn "Idle CPU is $idleCpu% (over the 5% bar). Something else is using this machine, and its hitches would be"
    Warn "recorded as ours with nothing in the bundle able to tell them apart afterwards."
    if ($Unattended -and -not $AllowBusyMachine) {
      throw "Aborted: machine not idle ($idleCpu% > 5%). Wait for it to settle (a just-finished AOT publish is a common cause) and re-run, or pass -AllowBusyMachine."
    }
    Warn "Continuing anyway. The measured value is recorded and the bundle is stamped untrusted."
  }
}
if (Get-Process -Name 'Wavee' -ErrorAction SilentlyContinue) {
  throw "Wavee is already running. Close it - two instances would fight over the same log and settings."
}

# ── PresentMon availability, PROBED ──────────────────────────────────────────────────────────────────────────
# An external present-side witness is optional but valuable, and its absence must be recorded WITH ITS REASON:
# "not installed" and "installed but no ETW rights" have different fixes, and both differ from "we never checked".
$presentMonPath = $null; $presentMonVersion = $null; $presentMonInstalled = $false
$presentMonUsable = $false; $presentMonReason = $null
$pmCandidate = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\presentmon.exe'
if (Test-Path $pmCandidate) { $presentMonPath = $pmCandidate }
else { $c = Get-Command 'presentmon.exe' -ErrorAction SilentlyContinue; if ($c) { $presentMonPath = $c.Source } }
if ($presentMonPath) {
  $presentMonInstalled = $true
  # 2.5.1 has no --version; it prints its banner on an unrecognised option, which is enough to identify it.
  try { $v = (& $presentMonPath --version 2>&1 | Out-String); if ($v -match 'PresentMon ([0-9.]+)') { $presentMonVersion = $Matches[1] } } catch { }
}
$idNow = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$prNow = New-Object System.Security.Principal.WindowsPrincipal($idNow)
$elevatedNow = $prNow.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
# S-1-5-32-559 = Performance Log Users, the non-admin route to an ETW session.
$inPerfGroup = [bool](@($idNow.Groups | Where-Object { $_.Value -eq 'S-1-5-32-559' }).Count)
$etwRights = ($elevatedNow -or $inPerfGroup)
if (-not $presentMonInstalled) { $presentMonReason = 'not installed (winget install Intel.PresentMon.Console)' }
elseif (-not $etwRights) { $presentMonReason = 'installed but this session has neither elevation nor Performance Log Users membership, so it cannot open an ETW session' }
else { $presentMonUsable = $true }
if ($presentMonUsable) { Info "PresentMon $presentMonVersion available (in-app DXGI/DWM stats still captured either way)" }
else {
  Warn "PresentMon unusable: $presentMonReason"
  Warn "Falling back to the IN-APP DXGI/DWM present statistics, which this build carries unconditionally."
}
if ($Unattended) {
  Warn "UNATTENDED / instrumentCheck: no human, no gestures. This validates the INSTRUMENT, not the feel."
}

# ── session directory ────────────────────────────────────────────────────────────────────────────────────────
# Name must not contain the literal tokens "Debug" or "Release": the rules at the top of .gitignore ignore any
# directory so named, which would swallow the bundle whole.
$utcStamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
if (-not $OutRoot) { $OutRoot = Join-Path $PSScriptRoot 'sessions' }
$sessionId = "$utcStamp-$shortSha"
$sess = Join-Path $OutRoot $sessionId
New-Item -ItemType Directory -Force -Path $sess | Out-Null
Step "Session $sessionId"
Info $sess

$scrollCsv = Join-Path $sess 'scroll.csv'
$consoleTxt = Join-Path $sess 'console.txt'
$outRaw = Join-Path $sess '.stdout.txt'
$errRaw = Join-Path $sess '.stderr.txt'
$phaseMarker = Join-Path $sess '.phase-marker.txt'
$phasesJsonl = Join-Path $sess 'phases.jsonl'
$abVariant = 0
if ($Opaque) { $abVariant = 1 }
# Stamp the free-scroll slice BEFORE launch so the first host-loop poll already has a phase ordinal.
WriteMarker $phaseMarker "1 1 $abVariant 0"

# ── environment ──────────────────────────────────────────────────────────────────────────────────────────────
# Every variable is set or CLEARED explicitly and recorded with its origin, so a bundle can never be read under
# the wrong assumption about what was on.
$envSet = [ordered]@{}
function SetEnv($name, $value, $why) {
  Set-Item -Path "Env:$name" -Value $value
  $envSet[$name] = [ordered]@{ value = $value; origin = 'overridden'; reason = $why }
}
function ClearEnv($name, $why) {
  if (Test-Path "Env:$name") { Remove-Item "Env:$name" }
  $envSet[$name] = [ordered]@{ value = $null; origin = 'explicitlyCleared'; reason = $why }
}

SetEnv 'FG_FPS_LOG' '1' 'the [fps] line: loop/present cadence, per-phase ms, wait kind, seam deltas'
SetEnv 'FG_SCROLL_PERF' '1' 'the [scrollperf] 1 Hz roll-up - the scrollBindThrash evidence'
SetEnv 'FG_WAKE_DIAG' '1' 'the reconciled / layout-only / record-only split and the wake-reason roster'
SetEnv 'FG_RENDER_CENSUS' '1' 'reconcile fan-out (suppressed unless flush >= 12ms or comps >= 25 - an empty census is NOT a refutation)'
# EXACTLY "1": this flag is read with a string comparison, not the usual EnvFlag helper, so 'true'/'on' silently
# disable it and the offsetDiscontinuity bucket comes back empty - which reads as "no discontinuities".
SetEnv 'FG_OFFSET_JUMP' '1' 'large single-write offset jumps; read as == "1" exactly, so true/on would DISABLE it'
SetEnv 'FG_LAYOUT_DIAG' '1' 'measure/arrange/text-shape counts; without it the FrameTiming i1 column is structurally 0'

if ($Diag) {
  SetEnv 'FG_SCROLL_TRACE' $scrollCsv 'the POD ring, written straight into the bundle (any value != "1" is used as a path)'
  SetEnv 'FG_SCROLL_PHASE_FILE' $phaseMarker 'one free-scroll slice marker, polled OUTSIDE the frame'
  SetEnv 'FG_RENDER_DIAG' '1' 'the [renderbudget] every-frame re-render roster'
  # These two are CompiledIn && !disabled - i.e. default ON once the symbol exists. Leaving them on would make the
  # diag build measurably different from the Release build being complained about, which invalidates the session.
  SetEnv 'FG_BIND_CONTRACT' '0' 'MANDATORY: default-ON once compiled in; would change the feel being measured'
  SetEnv 'FG_BACKWARDS_WRITE' '0' 'MANDATORY: default-ON once compiled in; does a subscriber-list scan per signal write'
  if ($Opaque) { SetEnv 'FG_OPAQUE_WINDOW' '1' 'A/B arm: opaque HWND swapchain instead of DWM Mica' } else { ClearEnv 'FG_OPAQUE_WINDOW' 'Mica arm' }
  if ($NoImagePump) { SetEnv 'FG_BISECT_NO_IMAGE_PUMP' '1' 'BISECTION arm: phase-7.5 image pump suppressed while scroll is active' } else { ClearEnv 'FG_BISECT_NO_IMAGE_PUMP' 'control arm: image pump normal' }
}
else {
  ClearEnv 'FG_SCROLL_TRACE' 'plain Release: the ring is compiled out'
  ClearEnv 'FG_SCROLL_PHASE_FILE' 'plain Release: no in-band state to stamp'
  ClearEnv 'FG_RENDER_DIAG' 'plain Release: RenderBudget is a no-op'
  ClearEnv 'FG_OPAQUE_WINDOW' 'plain Release: compile-fenced'
  ClearEnv 'FG_BISECT_NO_IMAGE_PUMP' 'plain Release: compile-fenced'
}

if ($GpuTiming) { SetEnv 'FG_GPU_TIMING' '1' 'Tier 2 opt-in: per-pass GPU attribution, at real per-frame cost' }
else { ClearEnv 'FG_GPU_TIMING' 'up to 256 extra EndQuery/frame, peaking during the very fling being measured' }
if ($PresentInterval0) { SetEnv 'FG_SCROLL_PRESENT_INTERVAL0' '1' 'paired arm with -GpuTiming' }
else { ClearEnv 'FG_SCROLL_PRESENT_INTERVAL0' 'not an independent switch' }

# FG_DIAG/FG_DIAG_CONSOLE are deliberately NOT in the default set: Diag.Count/Set concatenate a string and box a
# value under one process-global lock, ~20 times per frame inside the submit path, on the render thread - inside
# the exact code being measured.
ClearEnv 'FG_DIAG' 'allocates + locks ~20x/frame on the render thread, inside the path being measured'
ClearEnv 'FG_DIAG_CONSOLE' 'identical to FG_DIAG - there is no events-only mode'
ClearEnv 'FG_MEM_DIAG' 'interval dumps - separate run'
ClearEnv 'FG_MEM_DIAG_SEC' 'interval dumps - separate run'
ClearEnv 'FG_ALLOC_DIAG' 'per-segment alloc probes - separate run'
ClearEnv 'FG_ALLOC_TYPES' 'process-global EventListener - separate run'
ClearEnv 'FG_SCROLL_LOG' 'per-event Console.WriteLine with AutoFlush - its own class doc warns it perturbs pacing'
ClearEnv 'FG_SCROLLLOG' 'recorder-side variant of the same'
ClearEnv 'FG_NOVSYNC' 'would remove the present pacing being measured'
# Resolve default-on pacing knobs instead of inheriting an invisible shell override. The manifest below records the
# resulting values, not an obsolete description of what an older Wavee build happened to request.
ClearEnv 'FG_ADAPTIVE_FPS' 'production default ON; capture must not inherit an unrecorded governor override'
ClearEnv 'FG_PRECISE_WAIT' 'production default ON; capture must not inherit an unrecorded wait-path override'
ClearEnv 'FG_ANIM_FPS' 'use Wavee runtime policy (30 focused+AC without energy saver; otherwise 24)'
ClearEnv 'WAVEE_FPS' 'app-side overlay - extra per-frame text work'
ClearEnv 'WAVEE_LOG_LEVEL' 'app logging noise'
ClearEnv 'WAVEE_LOG_FILE_LEVEL' 'app logging noise'

Step "Environment"
foreach ($k in $envSet.Keys) {
  $v = $envSet[$k]
  if ($v.origin -eq 'overridden') { Info "$k=$($v.value)" }
}
Info "cleared: $((($envSet.Keys | Where-Object { $envSet[$_].origin -eq 'explicitlyCleared' }) -join ', '))"

# ── launch ───────────────────────────────────────────────────────────────────────────────────────────────────
# stdout and stderr are captured to SEPARATE files and merged afterwards rather than teed through a pipeline:
# a pipeline blocks until the process exits, which would make waiting on the window-close below impossible.
# Nothing is lost - crucially NOT stdout, where the [scrolltrace] banner goes, and which a bare '2>' redirect
# drops (that is why the previously committed capture has 476 [fps] lines and zero scrolltrace lines). The
# cross-stream ORDER is recovered from the tMs= prefix every diagnostic line carries, not from file order.
Step "Launching Wavee"
$proc = Start-Process -FilePath $ExePath -PassThru -RedirectStandardOutput $outRaw -RedirectStandardError $errRaw -WorkingDirectory (Split-Path $ExePath)
Info "pid $($proc.Id)"

Start-Sleep -Seconds 3
if ($proc.HasExited) { throw "Wavee exited immediately (code $($proc.ExitCode)). See $errRaw" }

# Verify the build is what the switches claim, by OBSERVATION rather than assumption.
$banner = $false
for ($i = 0; $i -lt 20; $i++) {
  if (Test-Path $outRaw) {
    $head = Get-Content $outRaw -TotalCount 200 -ErrorAction SilentlyContinue
    if ($head -and ($head | Where-Object { $_ -match '^\[scrolltrace\] writing to ' })) { $banner = $true; break }
  }
  Start-Sleep -Milliseconds 500
}
if ($Diag -and -not $banner) {
  Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
  throw "No '[scrolltrace] writing to ...' banner: this exe is NOT a FLUENTGPU_DIAG build. Publish with -Diag (or drop -Diag and accept a console-only session)."
}
if ($Diag) { Info "diag build confirmed (scrolltrace banner seen)" }

# ── free-scroll: operator uses the app, then closes the window ───────────────────────────────────────────────
# Gesture idle/drag/inertia still come from the engine's own state word.
$startWall = (Get-Date).ToUniversalTime().ToString('o')
$startQpc = [System.Diagnostics.Stopwatch]::GetTimestamp()

Say ""
if ($Unattended) {
  Step "Instrument check: idling $UnattendedSeconds s, then closing Wavee"
  Start-Sleep -Seconds $UnattendedSeconds
  [void]$proc.CloseMainWindow()
}
else {
  Say "Wavee is running. Use it however you want. Close the window when you are done."
  Warn "Do not kill it from Task Manager: the trace flushes on process exit, and a kill loses the tail."
}

$waited = 0
$maxWaitSec = 8 * 3600
while (-not $proc.HasExited -and $waited -lt $maxWaitSec) {
  Start-Sleep -Seconds 2
  $waited += 2
}
if (-not $proc.HasExited) {
  Warn "Still running after 8 hours; forcing. The scroll.csv tail may be truncated."
  Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 2
}
Info "exited (code $($proc.ExitCode))"

$endQpc = [System.Diagnostics.Stopwatch]::GetTimestamp()
$endWall = (Get-Date).ToUniversalTime().ToString('o')
WriteMarker $phaseMarker "0 1 $abVariant 0"

$phaseRecords = @(
  [ordered]@{
    ord = 1; name = 'freeScroll'; repetition = 1; coldPass = $false
    abVariant = $abVariant; abVariantName = $(if ($abVariant -eq 1) { 'opaque' } else { 'mica' })
    synthetic = [bool]$Unattended
    wallStartUtc = $startWall; wallEndUtc = $endWall
    startQpc = $startQpc; endQpc = $endQpc
    gluedScore1to5 = $null; steadyScore1to5 = $null; note = ''
    instruction = 'use the app; close the window when done'
  }
)

# ── assemble the bundle ──────────────────────────────────────────────────────────────────────────────────────
Step "Assembling bundle"
$outLines = @(); $errLines = @()
if (Test-Path $outRaw) { $outLines = Get-Content $outRaw }
if (Test-Path $errRaw) { $errLines = Get-Content $errRaw }
# stderr first (the anchor + every diagnostic stream), then stdout (the banner). Order across the two streams is
# recovered from tMs=, never from position in this file.
$merged = @()
$merged += "# ops/diag console.txt - stderr then stdout, merged. Cross-stream order comes from the tMs= prefix."
$merged += $errLines
$merged += $outLines
Set-Content -Path $consoleTxt -Value $merged -Encoding utf8
Remove-Item $outRaw -ErrorAction SilentlyContinue
Remove-Item $errRaw -ErrorAction SilentlyContinue
Remove-Item $phaseMarker -ErrorAction SilentlyContinue

$jsonl = @()
foreach ($r in $phaseRecords) { $jsonl += ($r | ConvertTo-Json -Depth 6 -Compress) }
[System.IO.File]::WriteAllLines($phasesJsonl, $jsonl, (New-Object System.Text.UTF8Encoding($false)))

# ── manifest ─────────────────────────────────────────────────────────────────────────────────────────────────
$gpu = $null
try { $gpu = Get-CimInstance Win32_VideoController -ErrorAction Stop | Select-Object -First 1 } catch { }
$cpu = $null
try { $cpu = Get-CimInstance Win32_Processor -ErrorAction Stop | Select-Object -First 1 } catch { }
$os = $null
try { $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop } catch { }
$batt = $null
try { $batt = Get-CimInstance Win32_Battery -ErrorAction Stop | Select-Object -First 1 } catch { }

$manifest = [ordered]@{
  # v2 makes the pacing knobs structured resolved values and records the launched target PID for PresentMon joins.
  schemaVersion = 2
  sessionId = $sessionId
  utcStart = $utcStamp
  utcEnd = (Get-Date).ToUniversalTime().ToString('o')
  launcherVersion = 3
  captureMode = $(if ($Unattended) { 'instrumentCheck' } else { 'freeScroll' })
  build = [ordered]@{
    gitSha = $gitSha; gitDirty = $gitDirty; gitBranch = $gitBranch
    # NOT an identity: InformationalVersion is a hand-edited literal in the csproj and does not move per build.
    informationalVersionNotAnIdentity = $true
    exeSha256 = $exeSha; exePath = $ExePath
    exeMtimeUtc = $exeInfo.LastWriteTimeUtc.ToString('o'); exeSizeBytes = $exeInfo.Length
    configuration = 'Release'; fluentGpuDiag = [bool]$Diag
    publishArgs = @($publishArgs); rid = "win-$Arch"; arch = $Arch
  }
  machine = [ordered]@{
    windowsBuild = $(if ($os) { $os.BuildNumber } else { $null })
    cpuModel = $(if ($cpu) { $cpu.Name } else { $null })
    coreCount = $(if ($cpu) { $cpu.NumberOfLogicalProcessors } else { $null })
    gpuAdapterDescription = $(if ($gpu) { $gpu.Name } else { $null })
    gpuDriverVersion = $(if ($gpu) { $gpu.DriverVersion } else { $null })
    totalRamMb = $(if ($os) { [math]::Round($os.TotalVisibleMemorySize / 1024) } else { $null })
    osArchitecture = "$([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)"
  }
  display = [ordered]@{
    # NOMINAL only. The MEASURED refresh period comes from DwmGetCompositionTimingInfo inside the app and lands
    # in the summary; a nominal Hz can be 0 or 1 on some drivers and is not a metric denominator.
    panelNominalHz = $(if ($gpu) { $gpu.CurrentRefreshRate } else { $null })
    horizontalPx = $(if ($gpu) { $gpu.CurrentHorizontalResolution } else { $null })
    verticalPx = $(if ($gpu) { $gpu.CurrentVerticalResolution } else { $null })
    qpcFrequency = [System.Diagnostics.Stopwatch]::Frequency
    vrrDetected = $null
    swapchainBufferCount = 2; maximumFrameLatency = 1; waitableUsed = $true
  }
  power = [ordered]@{
    acLineStatus = $(if ($batt) { 'battery-present' } else { 'ac-or-desktop' })
    batteryPct = $(if ($batt) { $batt.EstimatedChargeRemaining } else { $null })
    idleCpuPctPreCapture = $idleCpu
  }
  env = $envSet
  # Resolved capture-time policy. These three environment variables were explicitly cleared above, so the values
  # below describe this process rather than whatever happened to be inherited by the PowerShell host. Wavee's
  # ambient policy is dynamic; record both reachable values instead of pretending the whole session ran at one rate.
  effectiveKnobs = [ordered]@{
    adaptiveFps = [ordered]@{
      enabled = $true
      resolvedFrom = 'engine default; FG_ADAPTIVE_FPS explicitly cleared'
    }
    preciseWait = [ordered]@{
      enabled = $true
      resolvedFrom = 'engine default; FG_PRECISE_WAIT explicitly cleared'
    }
    bindContract = $(if ($Diag) { 'explicitly disabled' } else { 'not compiled in' })
    backwardsWrite = $(if ($Diag) { 'explicitly disabled' } else { 'not compiled in' })
    ambientFps = [ordered]@{
      mode = 'Wavee power/attention policy; FG_ANIM_FPS explicitly cleared'
      focusedAcNoEnergySaver = 30
      backgroundBatteryOrEnergySaver = 24
      mayChangeDuringSession = $true
    }
    gpuTiming = [bool]$GpuTiming
    layoutDiag = $true
    opaqueWindow = [bool]$Opaque
    presentInterval0 = [bool]$PresentInterval0
    # A bisection arm makes this bundle a TREATMENT, not an observation. It is not comparable to anything except a
    # control captured with otherwise identical switches, and it must never be read as "how the app behaves".
    bisectNoImagePump = [bool]$NoImagePump
  }
  # Probed, not assumed. "available: false" with no reason is indistinguishable from "we never looked", and the two
  # imply different follow-ups: install the tool, versus grant ETW rights, versus fall back to the in-app DXGI/DWM
  # present statistics (which this build carries unconditionally precisely so an unavailable PresentMon degrades the
  # bundle rather than voiding it).
  presentMon = [ordered]@{
    available = $presentMonUsable
    installed = $presentMonInstalled
    path = $presentMonPath
    version = $presentMonVersion
    targetProcessId = $proc.Id
    etwRightsAvailable = $etwRights
    unavailableReason = $presentMonReason
    argv = @()
  }
  switches = [ordered]@{
    diag = [bool]$Diag; opaque = [bool]$Opaque; gpuTiming = [bool]$GpuTiming
    presentInterval0 = [bool]$PresentInterval0; skipPublish = [bool]$SkipPublish
    noImagePump = [bool]$NoImagePump
    unattended = [bool]$Unattended
  }
  subjectiveScores = @()
}
WriteJsonNoBom $manifest (Join-Path $sess 'manifest.json')

Say ""
Step "Bundle: $sess"
Get-ChildItem $sess | ForEach-Object { Info ("{0,-20} {1,10} bytes" -f $_.Name, $_.Length) }

if (-not $NoPack) {
  Say ""
  & (Join-Path $PSScriptRoot 'pack-feel-summary.ps1') -Session $sess
}
