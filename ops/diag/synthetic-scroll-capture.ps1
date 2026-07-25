<#
.SYNOPSIS
  Drive a real Wavee process with SYNTHETIC wheel input and capture a scroll trace, unattended.

.DESCRIPTION
  The interactive session (wavee-scroll-session.ps1) is the authority on FEEL, because feel is a human
  measurement. This script is the authority on CADENCE, which is a machine measurement: it injects a
  deterministic, reproducible input stream via SendInput and captures the same scroll.csv, so two builds
  can be compared without a person in the loop and without run-to-run gesture variation.

  What it CAN answer: production-vs-present ratio, presented sample-time jitter, present interval
  distribution, clock-sample skew spread. These are properties of the frame loop and are independent of
  which input device drove it.

  What it CANNOT answer: anything about feel, and anything specific to the DirectManipulation touchpad
  path. SendInput synthesizes WM_MOUSEWHEEL, which reaches the engine's wheel / hi-res-fallback classifier;
  DM contacts arrive as real HID packets and cannot be synthesized. So a bundle from this script is
  labelled synthetic and must never be read as a feel result - see ops/diag/AGENT.md.

  Timing note: the injector paces itself with a sleep-then-spin loop, so it does consume some CPU. That is
  a constant across arms (same harness, same script), which is what makes the A/B valid; it is not a claim
  that the machine was idle.

.EXAMPLE
  ops\diag\synthetic-scroll-capture.ps1 -ExePath <path to Wavee.exe> -Label after
#>
#requires -Version 5.1
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$ExePath,
  [Parameter(Mandatory = $true)][string]$Label,
  [string]$OutRoot,
  # The repo that produced -ExePath. Discovered by walking up from the exe when omitted. This must NOT default to the
  # harness's own working directory: doing so recorded the harness branch for every arm, so a BEFORE bundle measuring
  # a baseline binary claimed to be the fix branch and the two arms were indistinguishable in their manifests.
  [string]$SourceRoot,
  [int]$Scale = 1
)
$ErrorActionPreference = 'Stop'

function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Info($m) { Write-Host "    $m" -ForegroundColor DarkGray }
function Warn($m) { Write-Host "    $m" -ForegroundColor Yellow }

if (-not (Test-Path $ExePath)) { throw "Wavee.exe not found: $ExePath" }
$ExePath = (Resolve-Path $ExePath).Path
if (-not $OutRoot) { $OutRoot = Join-Path $PSScriptRoot 'sessions' }
$sess = Join-Path $OutRoot ("synth-{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $Label)
New-Item -ItemType Directory -Force -Path $sess | Out-Null

Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

public static class SynthInput
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public MOUSEINPUT mi; }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    public static void Close(IntPtr h) { PostMessageW(h, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero); }
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    const uint MOUSEEVENTF_WHEEL = 0x0800;

    public static uint Wheel(int delta)
    {
        var i = new INPUT[1];
        i[0].type = 0;
        i[0].mi.mouseData = unchecked((uint)delta);
        i[0].mi.dwFlags = MOUSEEVENTF_WHEEL;
        return SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
    }

    // Sleep-then-spin pacing: Thread.Sleep alone quantizes to the system timer (~15 ms), which cannot express
    // an 8 ms packet cadence at all - the whole stream would collapse into bursts and measure the wrong thing.
    public static void Sleep(double ms)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            double left = ms - sw.Elapsed.TotalMilliseconds;
            if (left <= 0) return;
            if (left > 2.0) Thread.Sleep(1); else Thread.SpinWait(200);
        }
    }

    /// <summary>Emit `count` wheel packets of `delta` spaced `intervalMs` apart, on an ABSOLUTE schedule so a
    /// slow packet does not push every later one late (which would show up as input jitter and be mistaken
    /// for engine jitter).</summary>
    public static void Stream(int delta, int count, double intervalMs)
    {
        var sw = Stopwatch.StartNew();
        for (int k = 0; k < count; k++)
        {
            double due = k * intervalMs;
            while (true)
            {
                double left = due - sw.Elapsed.TotalMilliseconds;
                if (left <= 0) break;
                if (left > 2.0) Thread.Sleep(1); else Thread.SpinWait(200);
            }
            Wheel(delta);
        }
    }
}
'@

# ---- environment: the measured posture -------------------------------------------------------------
$csv = Join-Path $sess 'scroll.csv'
$marker = Join-Path $sess 'phase.marker'
Set-Content -Path $marker -Value "0 0 0 0" -Encoding ascii

$env:FG_SCROLL_TRACE      = $csv
$env:FG_SCROLL_PHASE_FILE = $marker
$env:FG_FPS_LOG           = '1'
# MANDATORY: both default ON once compiled with FLUENTGPU_DIAG, and both change the behaviour being measured.
$env:FG_BIND_CONTRACT     = '0'
$env:FG_BACKWARDS_WRITE   = '0'
# Never in a cadence session: Diag.Count/Set take a process-global lock ~20x/frame ON THE RENDER THREAD,
# inside the exact path being measured.
$env:FG_DIAG              = $null
$env:FG_DIAG_CONSOLE      = $null

Step "Launching $([System.IO.Path]::GetFileName($ExePath))  [$Label]"
$console = Join-Path $sess 'console.txt'
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $ExePath
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$proc = [System.Diagnostics.Process]::Start($psi)
$sb = New-Object System.Text.StringBuilder
$onOut = { if ($EventArgs.Data) { [void]$Event.MessageData.AppendLine($EventArgs.Data) } }
Register-ObjectEvent -InputObject $proc -EventName OutputDataReceived -Action $onOut -MessageData $sb | Out-Null
Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived  -Action $onOut -MessageData $sb | Out-Null
$proc.BeginOutputReadLine(); $proc.BeginErrorReadLine()

# ---- wait for a usable window ----------------------------------------------------------------------
$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 120; $i++) {
  Start-Sleep -Milliseconds 500
  $proc.Refresh()
  if ($proc.HasExited) { throw "Wavee exited during startup. Console:`n$($sb.ToString())" }
  if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $proc.MainWindowHandle; break }
}
if ($hwnd -eq [IntPtr]::Zero) { throw "No main window after 60 s." }
Info "window 0x$($hwnd.ToString('X'))"
Start-Sleep -Seconds 8   # let first paint, art decode and the home shelf settle before measuring

$r = New-Object SynthInput+RECT
[void][SynthInput]::GetWindowRect($hwnd, [ref]$r)
$cx = [int](($r.Left + $r.Right) / 2)
$cy = [int]($r.Top + ($r.Bottom - $r.Top) * 0.62)
[void][SynthInput]::SetForegroundWindow($hwnd)
[void][SynthInput]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 700
Info "cursor at $cx,$cy in window rect $($r.Left),$($r.Top)-$($r.Right),$($r.Bottom)"

function Phase([int]$ord, [string]$name, [scriptblock]$body) {
  Set-Content -Path $marker -Value "$ord 1 0 0" -Encoding ascii
  Info "phase $ord $name"
  [void][SynthInput]::SetCursorPos($cx, $cy)
  & $body
  Set-Content -Path $marker -Value "0 1 0 0" -Encoding ascii
  [SynthInput]::Sleep(900)   # let the chase/fling settle before the next phase
}

Step "Injecting the synthetic gesture script"
# EVERY moving phase SAWTOOTHS (down, then back up). The first version of this script scrolled one way only
# and measured almost nothing: the page hit its bottom four seconds in, after which every packet was a no-op
# against the extent clamp - 3.5 s of real scroll out of a 28 s run, with phases 3-5 producing zero rows.
# Reversing keeps the viewport in the interior of the content, which is also where scroll feel is judged
# (edge rubber-banding is its own regime and would contaminate a cadence measurement with band physics).
function Saw([int]$delta, [int]$legPackets, [double]$intervalMs, [int]$legs) {
  for ($l = 0; $l -lt $legs; $l++) {
    $d = if ($l % 2 -eq 0) { -$delta } else { $delta }
    [SynthInput]::Stream($d, $legPackets, $intervalMs)
    [SynthInput]::Sleep(120)   # brief settle so a reversal is a reversal, not a collision
  }
}

# 1 idle: the noise floor every cadence metric is measured against.
Phase 1 'idleFirst'   { [SynthInput]::Sleep(3000) }
# 2 hi-res flicks: sub-detent packets at touchpad cadence, in bursts - the fling/chase regime.
Phase 2 'flickBursts' { for ($b = 0; $b -lt 8; $b++) { $d = if ($b % 2 -eq 0) { -40 } else { 40 }; [SynthInput]::Stream($d, 12, 8.0); [SynthInput]::Sleep(600) } }
# 3 detented notches: the classic wheel path, one crit-damped chase per notch.
Phase 3 'wheelNotches'{ for ($b = 0; $b -lt 20; $b++) { $d = if ([math]::Floor($b / 5) % 2 -eq 0) { -120 } else { 120 }; [void][SynthInput]::Wheel($d); [SynthInput]::Sleep(300) } }
# 4 slow continuous pan: low velocity, where per-frame position error is not masked by speed.
Phase 4 'slowPan'     { Saw -delta 12 -legPackets 90 -intervalMs 10.0 -legs 10 }
# 5 fast continuous pan: high velocity, where jitter in the sampling instant is amplified into DIP.
Phase 5 'fastPan'     { Saw -delta 60 -legPackets 70 -intervalMs 8.0 -legs 14 }
# 6 idle: MUST run - the trace ring flushes on idle frames, and this is what writes the tail.
Phase 6 'idleLast'    { [SynthInput]::Sleep(5000) }

Step "Closing gracefully (never a kill first - the ring flushes on ProcessExit)"
[void]$proc.CloseMainWindow()
$w = 0
while (-not $proc.HasExited -and $w -lt 12) { Start-Sleep -Seconds 1; $w++ }
if (-not $proc.HasExited) {
  # CloseMainWindow posts to the process's own idea of a main window, which a custom-frame host can decline.
  # A direct WM_CLOSE to the HWND we actually drove is the same request through the door we know is open.
  Info "CloseMainWindow declined; posting WM_CLOSE to the captured HWND"
  [SynthInput]::Close($hwnd)
  $w = 0
  while (-not $proc.HasExited -and $w -lt 20) { Start-Sleep -Seconds 1; $w++ }
}
if (-not $proc.HasExited) {
  # The idleLast phase above already gave the ring its idle flush, so the CSV is written; only records
  # produced after that flush are lost. Say so rather than implying the capture is intact.
  Warn "still running; killing. Records after the last idle flush are lost (the idleLast phase flushed the body)."
  $proc.Kill()
}
Start-Sleep -Milliseconds 800

Set-Content -Path $console -Value $sb.ToString() -Encoding utf8
# ---- provenance: identify the SOURCE that produced this exe, not the harness ------------------------
if (-not $SourceRoot) {
  # Walk up from the exe looking for the solution marker. Publish layouts nest ~6 deep
  # (<root>\src\apps\Wavee\bin\...\publish\Wavee.exe), so a bounded climb is enough.
  $d = Split-Path $ExePath -Parent
  for ($i = 0; $i -lt 12 -and $d; $i++) {
    if (Test-Path (Join-Path $d 'src\FluentGpu.slnx')) { $SourceRoot = $d; break }
    $d = Split-Path $d -Parent
  }
}
$gitSha = $null; $gitBranch = $null; $gitDirty = $null
if ($SourceRoot -and (Test-Path $SourceRoot)) {
  $SourceRoot = (Resolve-Path $SourceRoot).Path
  $gitSha    = (& git -C $SourceRoot rev-parse HEAD 2>$null)
  $gitBranch = (& git -C $SourceRoot rev-parse --abbrev-ref HEAD 2>$null)
  $status    = (& git -C $SourceRoot status --porcelain 2>$null)
  if ($null -ne $gitSha) { $gitDirty = [bool]($status) }
}
else {
  Warn "Could not locate the source root for $ExePath - the bundle will be stamped UNIDENTIFIED and the analyzer will refuse to compare it."
}
# SHA-256 of the actual binary: the only identity that cannot be wrong. Two arms with the same hash are the same
# build, whatever their manifests claim.
$exeItem = Get-Item $ExePath
$exeHash = (Get-FileHash $ExePath -Algorithm SHA256).Hash

$manifest = [ordered]@{
  label      = $Label
  synthetic  = $true
  exePath    = $ExePath
  exeSize    = $exeItem.Length
  exeMtimeUtc= $exeItem.LastWriteTimeUtc.ToString('o')
  exeSha256  = $exeHash
  sourceRoot = $SourceRoot
  gitSha     = $gitSha
  gitBranch  = $gitBranch
  gitDirty   = $gitDirty
  identified = [bool]($gitSha)
  capturedUtc= (Get-Date).ToUniversalTime().ToString('o')
  note       = 'SYNTHETIC wheel input via SendInput. Valid for CADENCE only; carries no feel verdict and does not exercise the DirectManipulation touchpad path.'
}
Info "source $(if ($gitSha) { "$($gitBranch)@$($gitSha.Substring(0,8))$(if ($gitDirty) { ' DIRTY' })" } else { 'UNIDENTIFIED' })  sha256 $($exeHash.Substring(0,12))"
[System.IO.File]::WriteAllText((Join-Path $sess 'manifest.json'), ($manifest | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))

if (-not (Test-Path $csv)) { throw "No scroll.csv was written - the build is probably not FLUENTGPU_DIAG." }
$rows = (Get-Content $csv | Measure-Object -Line).Lines

# Coverage assertion. A capture whose input never actually moved the viewport looks EXACTLY like a capture of
# a perfectly smooth app: few rows, no jitter, nothing to report. That failure mode already bit this script
# once (one-directional input pinned the page at its extent and 4 of 5 moving phases recorded nothing), so it
# is checked here rather than left for the analysis to misread as a result.
$lat = @(Import-Csv $csv | Where-Object { $_.kind -eq 'latency' })
$movingPhases = @($lat | ForEach-Object { [int]$_.state -band 0xF } | Where-Object { $_ -ge 2 -and $_ -le 5 } |
                 Group-Object | Where-Object { $_.Count -ge 50 })
Step "Done: $sess"
Info "scroll.csv $rows rows   latency $($lat.Count)   moving phases with >=50 rows: $($movingPhases.Count)/4"
if ($lat.Count -lt 400 -or $movingPhases.Count -lt 3) {
  Warn "THIN CAPTURE. Fewer than 400 latency rows, or under 3 of the 4 moving phases produced data."
  Warn "Most likely the viewport sat against an extent clamp, or the window never took foreground."
  Warn "Do NOT compare this bundle - re-run it."
}
Write-Host $sess
