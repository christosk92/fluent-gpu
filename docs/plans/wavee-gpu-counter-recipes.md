# Universal GPU default — Get-Counter recipes

Continuous per-PID GPU timelines (never Task Manager’s 1 s smear). Run while Wavee is open; filter to the Wavee PID.

## Playing spike (A1)

```powershell
# Terminal A — start Wavee with wake/fps diagnostics
$env:FG_WAKE_DIAG = '1'
$env:FG_FPS_LOG = '1'
# optional: $env:FG_GPU_TIMING = '1'   # after mid-stamp + freshness fix

dotnet run --project src/apps/Wavee
```

```powershell
# Terminal B — continuous engtype_3d for the Wavee PID
$pid = (Get-Process Wavee -ErrorAction SilentlyContinue | Select-Object -First 1).Id
if (-not $pid) { throw 'Wavee not running' }
Get-Counter '\GPU Engine(*)\Utilization Percentage' -Continuous |
  Where-Object {
    $_.CounterSamples |
      Where-Object { $_.InstanceName -match "pid_${pid}" -and $_.InstanceName -match 'engtype_3d' -and $_.CookedValue -gt 0 }
  } |
  ForEach-Object {
    $s = $_.CounterSamples | Where-Object { $_.InstanceName -match "pid_${pid}" -and $_.InstanceName -match 'engtype_3d' }
    '{0:HH:mm:ss.fff}  {1:n1}%' -f $_.Timestamp, ($s | Measure-Object CookedValue -Sum).Sum
  }
```

**Expect after A1:** paused ~0%; playing + EQ visible ~8–15% (not ~56%). `[fps]` wait token ambient-class; no `sole: frameClockPoller` with lyrics closed.

## Scroll (B4 → B3)

Same counter recipe while scrolling the artist/album detail list maximized.

| Milestone | Scroll `engtype_3d` (order-of-magnitude) |
|---|---|
| Before | ~50–63% |
| +B4 opaque content | ~30–40% |
| +B3 occlusion inset | ~15–25% |

**B4 eyeball:** content plate opaque; titlebar/sidebar/player bar still show live Mica; TL corner cut-away preserved.

## Gates (agent)

```powershell
dotnet build src/FluentGpu.slnx
dotnet build src/FluentGpu.slnx -c Release
dotnet run --project src/FluentGpu.VerticalSlice
```
