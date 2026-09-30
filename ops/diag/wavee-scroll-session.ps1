<#
.SYNOPSIS
  Run ONE free-scroll Wavee capture and emit a self-describing session bundle.

.DESCRIPTION
  Launch Wavee with the feel-instrument engine switches, let the operator scroll however they want, then pack
  console.txt when they close the window. No gesture script, no ENTER-when-ready, no 1-5 ratings.

  "Smooth scroll" is two independent properties, and they trade against each other:

    Pillar A - glued:  does the content sit where the finger is?     (input -> offset commit)
    Pillar B - steady: are submit-confirmed presents evenly paced?   (offset -> record -> publish -> present)

  A pacing queue improves B and worsens A. Keep them structurally separate. Do not invent a fused
  smoothness score, and do not ask a human to rate either pillar - the traces already contain both.

  What this script produces (ops/diag/sessions/<utcStamp>-<sha>/):
    manifest.json   build + machine + display + power + the engine switches (--fg ...) the session launched with
    console.txt     stdout AND stderr, merged; MUST contain [fps] lines (the --fg fps switch armed)
    (per-input scroll traces: the Wavee Diagnostics Scroll card's CSV export - ScrollProbe level Trace)
    phases.jsonl    one freeScroll slice covering the whole session (wall clock + QPC; scores are always null)
  Scoring: the engine owns both pillars as ScrollMetrics over the ScrollProbe stream - Wavee logs the per-burst
  `scroll.burst` verdict, the Diagnostics Scroll card exports the probe CSV, and the Scroll Lab
  (ops/diag/scroll-lab-synthetic.ps1) scores synthetic A/B sessions. The ScrollTrace-era packer
  (pack-feel-summary.ps1 + AGENT.md) is retired with ScrollTrace.

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
  # Build WITH FLUENTGPU_DIAG. Without it there is no [renderbudget] roster - the
  # console streams still work.
  [switch]$Diag,
  # A/B arm: replace the DWM Mica composition path with an opaque HWND swapchain (`--fg opaque`).
  [switch]$Opaque,
  # Start with the pass-granular GPU timeline on (`--fg gpu-timing`, the AppHost.GpuPassTimingEnabled runtime toggle).
  # Its per-pass timestamps cost real GPU work on exactly the frames being measured, so it is off unless the GPU is
  # already implicated.
  [switch]$GpuTiming,
  # Present at sync-interval 0 (`--fg no-vsync`): separates the present cap from the frame cost.
  [switch]$PresentInterval0,
  [switch]$SkipPublish,
  [string]$ExePath,
  [string]$OutRoot,
  # UNATTENDED: launch, idle briefly, close. Stamped instrumentCheck / synthetic. Validates the toolchain
  # (diag build armed, anchor landed, streams merged). Nobody scrolled, so it cannot answer
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
# (Every arm is a runtime engine switch now - none is compile-fenced, so no arm depends on -Diag.)

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

$consoleTxt = Join-Path $sess 'console.txt'
$outRaw = Join-Path $sess '.stdout.txt'
$errRaw = Join-Path $sess '.stderr.txt'
$phaseMarker = Join-Path $sess '.phase-marker.txt'
$phasesJsonl = Join-Path $sess 'phases.jsonl'
$abVariant = 0
if ($Opaque) { $abVariant = 1 }
# Stamp the free-scroll slice BEFORE launch so the first host-loop poll already has a phase ordinal.
WriteMarker $phaseMarker "1 1 $abVariant 0"

# ── engine switches ──────────────────────────────────────────────────────────────────────────────────────────
# The engine reads NO environment variables: its diagnostic switches are the `--fg name,...` list on the command line
# (FluentGpu.Hosting.EngineSwitches, applied by FluentApp before the window exists). Every switch this session turns on
# is recorded with its reason, so a bundle can never be read under the wrong assumption about what was on. The wake
# census, the render census and the tile census are always on and need no switch.
$fgSet = [ordered]@{}
function FgOn($name, $why) { $fgSet[$name] = $why }

FgOn 'fps' 'the [fps] line: loop/present cadence, per-phase ms, wait kind, seam deltas'
FgOn 'layout' 'measure/arrange/text-shape counts; without it the FrameTiming i1 column is structurally 0'
if ($Diag) {
  FgOn 'render' 'the [renderbudget] every-frame re-render roster'
  # The DEBUG guards are default-ON once compiled in. Leaving them on would make the diag build measurably different
  # from the Release build being complained about, which invalidates the session.
  FgOn 'no-guards' 'MANDATORY in a diag build: BindContract / BackwardsWriteGuard scans would change the feel being measured'
}
if ($Opaque) { FgOn 'opaque' 'A/B arm: opaque HWND swapchain instead of DWM Mica' }
if ($GpuTiming) { FgOn 'gpu-timing' 'per-pass GPU attribution, at real per-frame cost' }
if ($PresentInterval0) { FgOn 'no-vsync' 'present at sync-interval 0' }
# NOT on by default: `diag` (Diag.Count/Set concatenate a string and box a value under one process-global lock, ~20
# times per frame on the render thread - inside the exact code being measured), `mem` / `alloc` / `alloc-types`
# (separate runs).
$fgArg = ($fgSet.Keys -join ',')

Step "Engine switches"
foreach ($k in $fgSet.Keys) { Info "--fg $k  ($($fgSet[$k]))" }

# ── launch ───────────────────────────────────────────────────────────────────────────────────────────────────
# stdout and stderr are captured to SEPARATE files and merged afterwards rather than teed through a pipeline:
# a pipeline blocks until the process exits, which would make waiting on the window-close below impossible.
# Nothing is lost - crucially NOT stdout, which a bare '2>' redirect
# drops. The
# cross-stream ORDER is recovered from the tMs= prefix every diagnostic line carries, not from file order.
Step "Launching Wavee"
$proc = Start-Process -FilePath $ExePath -ArgumentList @('--fg', $fgArg) -PassThru -RedirectStandardOutput $outRaw -RedirectStandardError $errRaw -WorkingDirectory (Split-Path $ExePath)
Info "pid $($proc.Id)"

Start-Sleep -Seconds 3
if ($proc.HasExited) { throw "Wavee exited immediately (code $($proc.ExitCode)). See $errRaw" }

# Verify the switches armed, by OBSERVATION rather than assumption: `--fg fps` prints an [fps] line within seconds.
$fpsSeen = $false
for ($i = 0; $i -lt 20; $i++) {
  if (Test-Path $errRaw) {
    $head = Get-Content $errRaw -TotalCount 400 -ErrorAction SilentlyContinue
    if ($head -and ($head | Where-Object { $_ -match '\[fps' })) { $fpsSeen = $true; break }
  }
  Start-Sleep -Milliseconds 500
}
if (-not $fpsSeen) { Warn "No [fps] line yet - the --fg switches may not have reached this build (older exe?)." }
else { Info "engine switches armed ([fps] line seen)" }

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
  Warn "Still running after 8 hours; forcing. The console tail may be truncated."
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
  engineSwitches = $fgSet
  engineSwitchArg = "--fg $fgArg"
  # Resolved capture-time policy. The engine reads no environment variables, so nothing inherited by the PowerShell
  # host can change these. Wavee's ambient policy is dynamic; record both reachable values instead of pretending the
  # whole session ran at one rate.
  effectiveKnobs = [ordered]@{
    adaptiveFps = [ordered]@{
      enabled = $true
      resolvedFrom = 'engine default (AppOptions.AdaptiveGpuPacing)'
    }
    preciseWait = [ordered]@{
      enabled = $true
      resolvedFrom = 'engine default (no --fg no-precise-wait)'
    }
    bindContract = $(if ($Diag) { 'disabled (--fg no-guards)' } else { 'not compiled in' })
    backwardsWrite = $(if ($Diag) { 'disabled (--fg no-guards)' } else { 'not compiled in' })
    ambientFps = [ordered]@{
      mode = 'Wavee power/attention policy'
      focusedAcNoEnergySaver = 30
      backgroundBatteryOrEnergySaver = 24
      mayChangeDuringSession = $true
    }
    gpuTiming = [bool]$GpuTiming
    layoutDiag = $true
    opaqueWindow = [bool]$Opaque
    presentInterval0 = [bool]$PresentInterval0
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
    unattended = [bool]$Unattended
  }
  subjectiveScores = @()
}
WriteJsonNoBom $manifest (Join-Path $sess 'manifest.json')

Say ""
Step "Bundle: $sess"
Get-ChildItem $sess | ForEach-Object { Info ("{0,-20} {1,10} bytes" -f $_.Name, $_.Length) }
