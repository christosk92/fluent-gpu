<#
.SYNOPSIS
  WS7 gallery shot sweep: capture a deterministic PNG for every registry page (and the whole shell), optionally
  pixel-diffing against a local baseline directory.

.DESCRIPTION
  1. Runs `--shot-list` to get the sweep contract (the shell id `gallery` + one `page:<key>` per registry page whose
     ShotMode != Skip).
  2. For each id, runs `--shot <id> --screenshot <out>` at 1240x820 (the WS7 gallery geometry) to render the scene
     deterministically (FG_SHOT=1: seeded demo data, paused ambient loops).
  3. If -BaselineDir is given and a baseline PNG exists for an id, reports a byte-length delta as a cheap change
     signal (a real per-pixel diff is a follow-up; baselines live OUT of the repo).

.EXAMPLE
  pwsh -File scripts/gallery-shot-sweep.ps1
  pwsh -File scripts/gallery-shot-sweep.ps1 -OutDir .tmp/shots -BaselineDir C:\gallery-baseline
#>
param(
  [string]$OutDir = ".tmp/shots",
  [string]$BaselineDir = "",
  [int]$Width = 1240,
  [int]$Height = 820,
  [string]$Project = "src/FluentGpu.WindowsApp"
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$env:FG_SHOT = "1"   # seeded demo data + paused ambient loops (deterministic capture)

Write-Host "Fetching shot list..."
$list = & dotnet run --project $Project -- --shot-list
$ids = @("gallery")
foreach ($line in $list) {
  $t = $line.Trim()
  if ($t -like "page:*") { $ids += ($t -split "`t")[0] }
}
$ids = $ids | Select-Object -Unique
Write-Host "Sweeping $($ids.Count) shot ids -> $OutDir (${Width}x${Height})"

$diffs = @()
foreach ($id in $ids) {
  $safe = ($id -replace "[:\\/*?""<>|]", "_")
  $out = Join-Path $OutDir "$safe.png"
  Write-Host "  shot $id -> $out"
  & dotnet run --project $Project -- --shot $id --screenshot $out --w $Width --h $Height | Out-Null

  if ($BaselineDir -ne "") {
    $base = Join-Path $BaselineDir "$safe.png"
    if ((Test-Path $base) -and (Test-Path $out)) {
      $bl = (Get-Item $base).Length; $nl = (Get-Item $out).Length
      if ($bl -ne $nl) { $diffs += "$id (baseline $bl B vs new $nl B)" }
    }
  }
}

if ($BaselineDir -ne "") {
  if ($diffs.Count -eq 0) { Write-Host "No size deltas vs baseline." }
  else { Write-Host "Changed vs baseline:"; $diffs | ForEach-Object { Write-Host "  $_" } }
}
Write-Host "Done. $($ids.Count) shots in $OutDir"
