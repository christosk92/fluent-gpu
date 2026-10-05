# Background, user-level end-to-end test of video in Wavee (PowerShell 5.1).
#   powershell -NoProfile -File ops\e2e\wavee-ui-e2e.ps1 [-Exe <Wavee.exe>] [-Clip <mp4>] [-OutRoot <dir>] [-Attach <pid>] [-Steps a,b,c,d,e]
# Launches Wavee `--fake --profile <fresh> --fake-video <clip> --fg test-input`, parks its window on the upper monitor WITHOUT activation and
# drives it with the engine's private TestInput window message (no real mouse, no real keys, no foreground changes, only windows of the
# process this script started). Every step is timed and followed by a PrintWindow screenshot into <OutRoot>\<timestamp>\.
# Layout constants are base (100 percent) pixels of the 1600x940 window on the Liked Songs page.
param(
  [string]$Exe = 'C:/wavee/vfix/int/WaveeMusic/src/apps/Wavee/bin/Release/net10.0/Wavee.exe',
  [string]$Clip = 'C:/wavee/vfix/e2e/testpattern-1080p.mp4',
  [string]$OutRoot = 'C:/wavee/vfix/e2e/runs',
  [int]$Attach = 0,
  [string]$ProfileOfAttach = '',
  [string[]]$Steps = @('a', 'b', 'c', 'd', 'e'),
  [switch]$AllowFullscreen   # off by default: one run sent the fullscreen window to the PRIMARY monitor (see report), which a background test must never do
)
$ErrorActionPreference = 'Stop'
$Steps = @($Steps | ForEach-Object { $_ -split ',' })
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$Run = Join-Path $OutRoot $stamp
New-Item -ItemType Directory -Force $Run | Out-Null
$ProfileDir = "C:/wavee/vfix/e2e/profile-$stamp"
$WX = 10; $WY = -1420; $WW = 1600; $WH = 940          # main window on the upper monitor (physical px; the upper monitor is 2560x1440 at 0,-1440)
$results = New-Object System.Collections.ArrayList
$script:shotN = 0
function NowMs { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }
function Step($id, $did, $saw, $timing, $status, $shot) {
  [void]$results.Add([pscustomobject]@{ step = $id; did = $did; saw = $saw; timing = $timing; status = $status; shot = $shot })
  Write-Host ("[{0}] {1} -> {2} | {3} | {4}" -f $id, $did, $status, $timing, $saw)
}
function SaveShot($hw, $name) { $script:shotN++; $f = "{0:D2}-{1}.png" -f $script:shotN, $name; Shot $hw (Join-Path $Run $f); $f }
function LogLines($since, $pattern) {
  $out = @()
  if (-not $script:log) { return $out }
  foreach ($l in (Get-Content $script:log -ErrorAction SilentlyContinue)) {
    if ($l -match ' t=(\d+) ' -and [int64]$Matches[1] -ge $since -and $l -match $pattern) { $out += $l }
  }
  $out
}
function LogT($line) { if ($line -match ' t=(\d+) ') { [int64]$Matches[1] } else { 0 } }
# Poll PrintWindow shots until the condition holds; returns @{ms; shot} (ms = elapsed since $t0, -1 on timeout).
function PollShot($hw, $t0, $name, [scriptblock]$cond, $timeoutMs = 6000) {
  $tmp = Join-Path $Run 'poll.tmp.png'
  while ($true) {
    Shot $hw $tmp
    $el = [int]((NowMs) - $t0)
    if (& $cond $tmp) { $f = SaveShot $hw $name; return @{ ms = $el; shot = $f } }
    if ($el -gt $timeoutMs) { $f = SaveShot $hw ($name + '-timeout'); return @{ ms = -1; shot = $f } }
  }
}
function HasVideoPixels($png, $x, $y, $w, $hh) { $s = RegionStats $png $x $y $w $hh; ($s.Colors -ge 120 -and $s.Std -gt 30 -and $s.Dark -lt 0.2) }
function ScrimOn($png, $x, $y, $w, $hh) { (RegionStats $png $x $y $w $hh).Mean -lt 100 }   # a control scrim darkens the video's bottom strip
function Sw($lines) { if ($lines -and $lines[0] -match 'sinceSwitchMs=(\d+)') { $Matches[1] } else { '?' } }
# The video placement menu (the chevron beside the video button): rows 0..4 = dock, mini player, separate window, full screen, off.
# The menu is its own popup HWND; its rows are clicked through the main window (the engine hit-tests popups in owner coordinates).
function Menu-Pick($index, $cx = 1393, $cy = 886) {
  Click $h $cx $cy; Start-Sleep -Milliseconds 700
  $pp = WinsOf $script:wpid | Where-Object { $_.Class -eq 'FluentGpuPopupWindow' } | Select-Object -First 1
  if (-not $pp) { return $false }
  $r = Rect $pp.H; $mr = Rect $h; [void](SaveShot $pp.H ("menu-open-row$index"))
  Click $h (($r.X - $mr.X) / $script:S + $r.W / $script:S / 2) (($r.Y - $mr.Y) / $script:S + 30 + 50.5 * $index); Start-Sleep -Milliseconds 600
  $true
}

# ---- launch ----
if ($Attach -gt 0) { $script:wpid = $Attach; $mh = Main-Hwnd $Attach; $ProfileDir = $ProfileOfAttach }
else { $p = Start-Wavee $Exe $ProfileDir $Clip; $script:wpid = $p.Id; $mh = Main-Hwnd $p.Id }
if (-not $mh) { throw 'no main window' }
$h = $mh
[void](Settle-Window $h $WX $WY $WW $WH)
$wr = Rect $h; $cr = ClientRect $h
Write-Host ("pid {0} hwnd {1} rect {2},{3} {4}x{5} client {6}x{7}" -f $script:wpid, $h.ToInt64(), $wr.X, $wr.Y, $wr.W, $wr.H, $cr.W, $cr.H)
Start-Sleep -Seconds 6
$script:log = Log-Path $ProfileDir

# ---- a: play, video appears ----
if ($Steps -contains 'a') {
  [void](SaveShot $h 'a0-home')
  Click $h 129 421; Start-Sleep -Seconds 3      # Liked Songs
  [void](SaveShot $h 'a0b-liked-songs')
  $t0 = NowMs; Click $h 453 504
  Start-Sleep -Milliseconds 1500
  $f = SaveShot $h 'a1-playing'
  $fa = @(LogLines $t0 'audio\.first-audio')
  $aud = if ($fa) { (LogT $fa[0]) - $t0 } else { -1 }
  Step 'a1' 'Click Play on Liked Songs' 'track 1 starts on the silent voice (the clip carries the sound); the bar shows title and running clock; rows carry a video badge; no picture yet because the video surface is off until asked' ("first audio {0} ms after the click (log audio.first-audio)" -f $aud) 'REVIEW' $f
  $t1 = NowMs; Click $h 1360 886
  $r = PollShot $h $t1 'a2-video-docked' { param($f) HasVideoPixels $f 1166 60 425 190 } 8000
  $ff = @(LogLines $t1 '\[video\] first\.frame')
  $logMs = if ($ff) { (LogT $ff[0]) - $t1 } else { -1 }
  Step 'a2' 'Click the video button in the player bar (docked rail)' 'the test pattern appears in the right rail above the Video title, 16:9, inside its frame' ("first frame per log {0} ms after click (sinceSwitchMs={1}); first PrintWindow poll showing it {2} ms (poll step ~100 ms)" -f $logMs, (Sw $ff), $r.ms) $(if ($r.ms -ge 0) { 'PASS' } else { 'FAIL' }) $r.shot
}

# ---- b: hover controls ----
if ($Steps -contains 'b') {
  Leave $h; Start-Sleep -Seconds 4
  $base = SaveShot $h 'b0-controls-hidden'
  $baseScrim = ScrimOn (Join-Path $Run $base) 1166 250 425 45
  $t0 = NowMs; Hover $h 1230 100 6 1000 500
  $r = PollShot $h $t0 'b1-hover-controls' { param($f) ScrimOn $f 1166 250 425 45 } 4000
  Step 'b1' 'Hover over the docked video' ("on-media controls (prev/pause/next, rail, time, volume) fade in over the picture; scrim before hover: {0}" -f $baseScrim) ("visible <= {0} ms after the first move (6 move steps of 16 ms included)" -f $r.ms) $(if ($r.ms -ge 0 -and -not $baseScrim) { 'PASS' } else { 'FAIL' }) $r.shot
  Start-Sleep -Milliseconds 800
  $t0 = NowMs; Leave $h
  $r = PollShot $h $t0 'b2-after-leave' { param($f) -not (ScrimOn $f 1166 250 425 45) } 8000
  Step 'b2' 'Pointer leaves the window' 'the controls fade out and the clean picture remains' ("hidden <= {0} ms after the leave" -f $r.ms) $(if ($r.ms -ge 0) { 'PASS' } else { 'FAIL' }) $r.shot
  Hover $h 1230 100 4 1000 500; Start-Sleep -Milliseconds 400
  $t0 = NowMs
  $r = PollShot $h $t0 'b3-idle-autohide' { param($f) -not (ScrimOn $f 1166 250 425 45) } 8000
  Step 'b3' 'Hover the video, then stop moving' 'the controls hide on their own after the pointer rests over the video' ("hidden <= {0} ms after the last move" -f $r.ms) $(if ($r.ms -ge 0) { 'PASS' } else { 'FAIL' }) $r.shot
}

# ---- c: controls ----
if ($Steps -contains 'c') {
  Hover $h 1380 150 4 1000 500; Start-Sleep -Milliseconds 700
  Click $h 1379 166     # on-media pause
  Start-Sleep -Milliseconds 700; $f1 = SaveShot $h 'c1-paused'; Start-Sleep -Milliseconds 1000; $f2 = SaveShot $h 'c1b-paused-1s-later'
  $d = (RegionStats (Join-Path $Run $f1) 1166 60 425 150).Mean - (RegionStats (Join-Path $Run $f2) 1166 60 425 150).Mean
  Step 'c1' 'Click the on-media pause button' 'the picture freezes and the bar button flips to play' ("two shots 1 s apart: mean luma delta {0:N2}; judged by eye" -f $d) 'REVIEW' $f2
  Hover $h 1380 150 3 1000 500; Click $h 1379 166
  Start-Sleep -Milliseconds 900; $f = SaveShot $h 'c2-resumed'
  Step 'c2' 'Click the on-media play button' 'playback resumes' 'n/a' 'REVIEW' $f
  $t0 = NowMs; Click $h 850 886
  Start-Sleep -Milliseconds 900; $f = SaveShot $h 'c3-seek-bar'
  $sk = @(LogLines $t0 'seek|audio\.cut|first\.frame')
  Step 'c3' 'Click the seek rail in the player bar at ~70 percent' 'the clock jumps to about 1:24 of 2:00 and the picture continues from there' ("{0} seek-related log lines" -f $sk.Count) 'REVIEW' $f
  $t0 = NowMs; Click $h 469 886
  $r = PollShot $h $t0 'c4-next' { param($f) HasVideoPixels $f 1166 60 425 190 } 8000
  $ff = @(LogLines $t0 '\[video\] first\.frame')
  Step 'c4' 'Click Next in the player bar' 'title changes and the clip restarts in the rail' ("click -> picture visible {0} ms; log first.frame sinceSwitchMs={1}" -f $r.ms, (Sw $ff)) $(if ($r.ms -ge 0) { 'PASS' } else { 'FAIL' }) $r.shot
  Start-Sleep -Seconds 2
  $t0 = NowMs; Click $h 378 886
  $r = PollShot $h $t0 'c5-prev' { param($f) HasVideoPixels $f 1166 60 425 190 } 8000
  $ff = @(LogLines $t0 '\[video\] first\.frame')
  Step 'c5' 'Click Previous in the player bar' 'previous track, clip in the rail' ("click -> picture visible {0} ms; log first.frame sinceSwitchMs={1}" -f $r.ms, (Sw $ff)) $(if ($r.ms -ge 0) { 'PASS' } else { 'FAIL' }) $r.shot
}

# ---- d: placements ----
if ($Steps -contains 'd') {
  $plans = @(@('mini', 1), @('docked', 0)); if ($AllowFullscreen) { $plans = @(@('mini', 1), @('fullscreen', 3), @('docked', 0)) }
  foreach ($pl in $plans) {
    $t0 = NowMs
    $ok = $false
    if ($pl[0] -eq 'docked' -and $AllowFullscreen) {   # from fullscreen: reveal the on-media controls and use their placement chevron
      $fr = Rect $h; Hover $h 1280 600 4 1000 300; Start-Sleep -Milliseconds 700
      $ok = Menu-Pick $pl[1] ([int]($fr.W / $script:S) - 191) ([int]($fr.H / $script:S) - 41)
    } else { $ok = Menu-Pick $pl[1] }
    if (-not $ok) { Step ('d-' + $pl[0]) ('Placement menu to ' + $pl[0]) 'menu popup did not open' 'n/a' 'FAIL' ''; continue }
    Start-Sleep -Milliseconds 150; $f0 = SaveShot $h ('d-' + $pl[0] + '-transition')
    Start-Sleep -Milliseconds 1300; $f = SaveShot $h ('d-' + $pl[0] + '-settled')
    $pls = (LogLines $t0 'video placement' | ForEach-Object { if ($_ -match 'video placement (.*?) requested') { $Matches[1] } }) -join '; '
    Step ('d-' + $pl[0]) ('Placement menu to ' + $pl[0]) 'see screenshot' ("placement log: {0}" -f $pls) 'REVIEW' $f
    if ($pl[0] -eq 'fullscreen') {
      $fr = Rect $h
      Hover $h 1280 600 4 1000 300; Start-Sleep -Milliseconds 700; [void](SaveShot $h 'd-fullscreen-controls')
      Leave $h; Start-Sleep -Seconds 4; [void](SaveShot $h 'd-fullscreen-hidden')
    }
    if ($pl[0] -eq 'docked') { Start-Sleep -Milliseconds 800; $rr = Settle-Window $h $WX $WY $WW $WH; [void](SaveShot $h 'd-docked-restored') }
  }
}

# ---- e: pop-out ----
if ($Steps -contains 'e') {
  function PopWin { WinsOf $script:wpid | Where-Object { $_.Class -ne 'FluentGpuPopupWindow' -and $_.H -ne $h } | Select-Object -First 1 }
  $popTimes = @()
  for ($round = 1; $round -le 3; $round++) {
    $t0 = NowMs
    if (-not (Menu-Pick 2)) { Step "e$round-open" 'Placement menu to separate window' 'menu popup did not open' 'n/a' 'FAIL' ''; continue }
    $pw = $null; $el = -1
    while (((NowMs) - $t0) -lt 8000) { $pw = PopWin; if ($pw) { $el = [int]((NowMs) - $t0); $firstRect = (Rect $pw.H); Place $pw.H 1600 -1430 ([int](640 * $script:S)) ([int](400 * $script:S)); break }; Start-Sleep -Milliseconds 50 }
    if (-not $pw) { Step "e$round-open" 'Placement menu to separate window' 'no second window appeared' 'timeout' 'FAIL' (SaveShot $h "e$round-nopopout"); continue }
    $ph = $pw.H
    $fgMine = Foreground-IsMine $script:wpid
    Settle-Window $ph 1600 -1430 640 400 | Out-Null   # right edge of the upper monitor; overlaps the main window by ~140 px
    $r = PollShot $ph $t0 "e$round-popout-video" { param($f) HasVideoPixels $f 0 60 640 280 } 8000
    $popTimes += $r.ms
    Step "e$round-open" 'Choose Play in a separate window' ("pop-out window first seen at {0},{1} {2}x{3} (before I moved it); picture in pop-out; main window shows the hole; foreground window belongs to Wavee: {4}" -f $firstRect.X, $firstRect.Y, $firstRect.W, $firstRect.H, $fgMine) ("window appeared {0} ms, picture in pop-out (poll) {1} ms after the menu click" -f $el, $r.ms) $(if ($r.ms -ge 0) { 'PASS' } else { 'FAIL' }) $r.shot
    [void](SaveShot $h "e$round-main-while-popped")
    if ($round -eq 1) {
      Hover $ph 320 200 5 60 300; Start-Sleep -Milliseconds 600
      $fh = SaveShot $ph 'e1-popout-hover-controls'
      Step 'e1-hover' 'Hover the pop-out video' 'on-media controls appear in the pop-out' 'n/a' 'REVIEW' $fh
      # WM_NCHITTEST on the title band and on controls (screen px)
      $pr = Rect $ph
      $hits = @()
      foreach ($pt in @(@(60, 12, 'title band left'), @(250, 12, 'title band mid'), @(500, 12, 'title band right'), @(320, 200, 'video centre'), @(320, 370, 'bottom controls'), @(2, 200, 'left resize edge'))) {
        $hits += ("{0}=0x{1:X}" -f $pt[2], (Nchit $ph ($pr.X + [int]($pt[0] * $script:S)) ($pr.Y + [int]($pt[1] * $script:S))))
      }
      Step 'e1-nchittest' 'Send WM_NCHITTEST over the pop-out (screen points)' 'HTCAPTION=0x2 expected on the title band, HTCLIENT=0x1 on controls' ($hits -join '; ') 'REVIEW' ''
      # simulate the OS move/size loop: ENTERSIZEMOVE, moves/resizes with MOVING/SIZING, EXITSIZEMOVE; the main window must keep presenting
      $lm = SaveShot $h 'e1-loop-main-before'; $lp = SaveShot $ph 'e1-loop-pop-before'
      [void][E2E.W]::SendMessage($ph, 0x231, [IntPtr]::Zero, [IntPtr]::Zero)
      $t0 = NowMs; $mainDiff = 0; $prev = $null
      for ($i = 1; $i -le 24; $i++) {
        $x = 1600 - $i * 6; $y = -1430 + [int](20 * [math]::Sin($i / 3)); $w = [int]((640 - $i * 2) * $script:S); $hh = [int]((400 - $i) * $script:S)
        Place $ph $x $y $w $hh
        if ($i % 6 -eq 0) { $png = Join-Path $Run ("loop-{0}.png" -f $i); Shot $h $png; Shot $ph (Join-Path $Run ("looppop-{0}.png" -f $i)) }
        Start-Sleep -Milliseconds 33
      }
      [void][E2E.W]::SendMessage($ph, 0x232, [IntPtr]::Zero, [IntPtr]::Zero)
      $loopMs = (NowMs) - $t0
      Start-Sleep -Milliseconds 800
      $le = SaveShot $ph 'e1-loop-pop-after'; $lme = SaveShot $h 'e1-loop-main-after'
      $a = RegionStats (Join-Path $Run 'loop-6.png') 520 875 100 25; $b = RegionStats (Join-Path $Run 'loop-24.png') 520 875 100 25
      $pv = HasVideoPixels (Join-Path $Run $le) 0 40 ([int]((Rect $ph).W - 40)) ([int]((Rect $ph).H - 80))
      Step 'e1-moveloop' 'Simulated move/size loop (ENTERSIZEMOVE, 24 SetWindowPos moves/resizes at 33 ms, EXITSIZEMOVE). The physical caption drag was NOT performed' ("picture in pop-out after the loop: {0}; main bar clock region luma {1} -> {2} (must change while it plays)" -f $pv, $a.Mean, $b.Mean) ("loop {0} ms" -f $loopMs) 'REVIEW' $le
      Remove-Item (Join-Path $Run 'loop-*.png') -ErrorAction SilentlyContinue
    }
    # pop back in: main window menu -> Dock in rail
    $t0 = NowMs
    [void](Menu-Pick 0)
    $r = PollShot $h $t0 "e$round-popin" { param($f) HasVideoPixels $f 1166 60 425 190 } 8000
    $gone = -not (PopWin)
    Step "e$round-close" 'Pop back in (placement menu to Dock in rail)' ("picture back in the rail; pop-out window gone: {0}" -f $gone) ("picture in rail {0} ms after the menu click" -f $r.ms) $(if ($r.ms -ge 0 -and $gone) { 'PASS' } else { 'FAIL' }) $r.shot
    Start-Sleep -Seconds 1
  }
}

# ---- logs + report ----
$stats = @{}
if ($script:log) {
  $all = @(Get-Content $script:log)
  $strip = { param($l) ($l -replace '^.*? [IWE] ', '') }
  $stats.budget = @($all | Where-Object { $_ -match 'switch\.budget' } | ForEach-Object { & $strip $_ })
  $stats.frames = @($all | Where-Object { $_ -match 'video.*(rendered|dropped)' } | ForEach-Object { & $strip $_ } | Select-Object -Last 20)
  $stats.pace = @($all | Where-Object { $_ -match 'render\.pace' } | ForEach-Object { & $strip $_ } | Select-Object -Last 6)
  $stats.popout = @($all | Where-Object { $_ -match 'pop-?out' } | ForEach-Object { & $strip $_ } | Select-Object -First 40)
  $stats.firstFrames = @($all | Where-Object { $_ -match '\[video\] first\.frame' } | ForEach-Object { & $strip $_ })
  $stats.warnings = @($all | Where-Object { $_ -match ' W \[video' } | ForEach-Object { & $strip $_ } | Select-Object -First 20)
}
[pscustomobject]@{ run = $stamp; pid = $script:wpid; profile = $ProfileDir; steps = $results; log = $stats } | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 (Join-Path $Run 'report.json')
$md = @("# Wavee video e2e ($stamp)", '', '| Step | What was done | What a user sees | Timing | Result | Screenshot |', '|---|---|---|---|---|---|')
foreach ($s in $results) { $md += ('| {0} | {1} | {2} | {3} | {4} | {5} |' -f $s.step, $s.did, $s.saw, $s.timing, $s.status, $s.shot) }
$md | Set-Content -Encoding UTF8 (Join-Path $Run 'report.md')
Remove-Item (Join-Path $Run 'poll.tmp.png') -ErrorAction SilentlyContinue
Write-Host "run folder: $Run  pid: $($script:wpid)"
