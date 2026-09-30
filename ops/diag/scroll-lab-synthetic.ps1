<#
.SYNOPSIS
  Record one SYNTHETIC Scroll Lab session and print its metrics — the unattended scroll-feel A/B.

.DESCRIPTION
  Launches the Scroll Lab, opens and closes the tuning window, records (F10) a ~10 s session driven by SendInput wheel
  packets (isolated detented notches, a spin, an F8 "felt wrong" marker, a reversal, four hi-res bursts), closes the lab
  and prints the session folder: events.csv (ScrollProbe.ExportCsv rows — inputs, poses, presents, turns, markers),
  frames.csv, markers.json and metrics.json (ScrollMetrics' verdicts: tracking AND cadence). Run it against two builds
  and compare metrics.json — the same input script drives both arms.

  This replaces the ScrollTrace-era synthetic-scroll-capture.ps1 / analyze-cadence.py / pack-feel-summary.ps1 pipeline
  (retired with ScrollTrace): the probe stream plus ScrollMetrics own both pillars now. A precision touchpad cannot be
  synthesized (no synthetic PT_TOUCHPAD device); touchpad feel is judged from live sessions (docs/guide/scroll-lab.md).

.EXAMPLE
  powershell -File ops\diag\scroll-lab-synthetic.ps1
  powershell -File ops\diag\scroll-lab-synthetic.ps1 -ExePath C:\publish\lab\FluentGpu.ScrollLab.exe
#>
param(
  [string]$ExePath = (Join-Path $PSScriptRoot '..\..\src\FluentGpu.ScrollLab\bin\Debug\net10.0\FluentGpu.ScrollLab.exe')
)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path $ExePath).Path
$sessions = Join-Path $env:LOCALAPPDATA 'FluentGpu\ScrollLab\sessions'
$before = @(); if (Test-Path $sessions) { $before = @(Get-ChildItem $sessions -Directory | ForEach-Object Name) }

Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
public static class Lab
{
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] public struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; [FieldOffset(8)] public KEYBDINPUT ki; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static void Close(IntPtr h) { PostMessageW(h, 0x0010, IntPtr.Zero, IntPtr.Zero); }
    public static int VisibleWindows(int pid)
    {
        int n = 0;
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) n++; return true; }, IntPtr.Zero);
        return n;
    }
    static uint Send(INPUT i) { return SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT))); }
    public static uint Wheel(int delta) { var i = new INPUT(); i.type = 0; i.mi.mouseData = unchecked((uint)delta); i.mi.dwFlags = 0x0800; return Send(i); }
    public static void Key(ushort vk, bool up) { var i = new INPUT(); i.type = 1; i.ki.wVk = vk; i.ki.dwFlags = up ? 2u : 0u; Send(i); }
    public static void Tap(ushort vk) { Key(vk, false); Thread.Sleep(30); Key(vk, true); }
    public static void Chord(ushort[] mods, ushort vk) { foreach (var m in mods) Key(m, false); Tap(vk); for (int k = mods.Length - 1; k >= 0; k--) Key(mods[k], true); }
    public static void Sleep(double ms) { var sw = Stopwatch.StartNew(); while (true) { double left = ms - sw.Elapsed.TotalMilliseconds; if (left <= 0) return; if (left > 2.0) Thread.Sleep(1); else Thread.SpinWait(200); } }
    public static void Stream(int delta, int count, double intervalMs) { var sw = Stopwatch.StartNew(); for (int k = 0; k < count; k++) { double due = k * intervalMs; while (true) { double left = due - sw.Elapsed.TotalMilliseconds; if (left <= 0) break; if (left > 2.0) Thread.Sleep(1); else Thread.SpinWait(200); } Wheel(delta); } }
}
'@

$errFile = Join-Path $env:TEMP 'scroll-lab-synthetic.stderr.txt'; $outFile = Join-Path $env:TEMP 'scroll-lab-synthetic.stdout.txt'
$proc = Start-Process -FilePath $exe -PassThru -RedirectStandardError $errFile -RedirectStandardOutput $outFile
$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 500; $proc.Refresh(); if ($proc.HasExited) { throw "lab exited during startup (code $($proc.ExitCode))" }; if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $proc.MainWindowHandle; break } }
if ($hwnd -eq [IntPtr]::Zero) { throw 'no main window after 30 s' }
"lab started: pid $($proc.Id) window 0x$($hwnd.ToString('X'))"
Start-Sleep -Seconds 3
$r = New-Object Lab+RECT; [void][Lab]::GetWindowRect($hwnd, [ref]$r)
$cx = [int]($r.Left + ($r.Right - $r.Left) * 0.35); $cy = [int]($r.Top + ($r.Bottom - $r.Top) * 0.55)
[void][Lab]::SetForegroundWindow($hwnd); [void][Lab]::SetCursorPos($cx, $cy); Start-Sleep -Milliseconds 500

# Tuning window (Ctrl+Shift+T)
$w0 = [Lab]::VisibleWindows($proc.Id)
[Lab]::Chord(@(0x11, 0x10), 0x54); Start-Sleep -Seconds 2
$w1 = [Lab]::VisibleWindows($proc.Id)
"visible top-level windows: before Ctrl+Shift+T = $w0, after = $w1"
[void][Lab]::SetForegroundWindow($hwnd); [void][Lab]::SetCursorPos($cx, $cy); Start-Sleep -Milliseconds 800

# Record (F10) a ~10 s synthetic session
[Lab]::Tap(0x79); Start-Sleep -Milliseconds 600
$sw = [Diagnostics.Stopwatch]::StartNew()
for ($k = 0; $k -lt 6; $k++) { [void][Lab]::Wheel(-120); [Lab]::Sleep(550) }                  # isolated notches (down)
[Lab]::Stream(-120, 20, 40.0); [Lab]::Sleep(900)                                             # a spin
[Lab]::Tap(0x77); [Lab]::Sleep(300)                                                          # F8 felt wrong
for ($k = 0; $k -lt 5; $k++) { [void][Lab]::Wheel(120); [Lab]::Sleep(450) }                  # reversal, isolated (up)
for ($b = 0; $b -lt 4; $b++) { $d = if ($b % 2 -eq 0) { -40 } else { 40 }; [Lab]::Stream($d, 12, 8.0); [Lab]::Sleep(500) }   # hi-res bursts
[Lab]::Sleep([Math]::Max(0, 10000 - $sw.ElapsedMilliseconds))
[Lab]::Tap(0x79); Start-Sleep -Seconds 3
"injected for $([int]$sw.ElapsedMilliseconds) ms"

[void][Lab]::SetForegroundWindow($hwnd); Start-Sleep -Milliseconds 500
$w2 = [Lab]::VisibleWindows($proc.Id)
[Lab]::Chord(@(0x11, 0x10), 0x54); Start-Sleep -Seconds 3
"visible top-level windows after the session: before Ctrl+Shift+T = $w2, after = $([Lab]::VisibleWindows($proc.Id))"
[Lab]::Close($hwnd)
if (-not $proc.WaitForExit(15000)) { 'lab did not exit on WM_CLOSE; killing'; $proc.Kill() } else { "lab exited with code $($proc.ExitCode)" }

$after = @(Get-ChildItem $sessions -Directory | Where-Object { $before -notcontains $_.Name })
Get-Content $errFile | Where-Object { $_ -match 'scroll-lab|exception|error' } | ForEach-Object { "stderr: $_" }
if ($after.Count -eq 0) { throw 'no session folder written' }
foreach ($d in $after) {
  "session folder: $($d.FullName)"
  Get-ChildItem $d.FullName | ForEach-Object { "  {0,-18} {1,10} bytes" -f $_.Name, $_.Length }
  "  events.csv rows: $((Get-Content (Join-Path $d.FullName 'events.csv')).Count)"
  "  frames.csv rows: $((Get-Content (Join-Path $d.FullName 'frames.csv')).Count)"
  "  markers.json: $((Get-Content (Join-Path $d.FullName 'markers.json') -Raw) -replace '\s+',' ')"
  $m = Get-Content (Join-Path $d.FullName 'metrics.json') -Raw | ConvertFrom-Json
  foreach ($x in $m) { "  [{0}] {1} = {2} {3} (baseline {4}; n={5}; F8 hits={6}) {7}" -f $x.verdict, $x.name, $x.value, $x.unit, $x.baseline, $x.samples, $x.markerHits, $x.detail }
}
