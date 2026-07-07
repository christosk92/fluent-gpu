<#
.SYNOPSIS
  Run Wavee CPU + memory benchmarks and write JSON results.

.DESCRIPTION
  Launches Wavee with --fake --perf-bench (offline, deterministic). Samples process CPU% and working set
  (Task-Manager-style) plus per-frame engine work time across idle, home, artist-detail, and nav-burst scenarios.

.EXAMPLE
  build\bench-wavee.cmd
  pwsh build/bench-wavee.ps1 -Exe app\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe
#>
[CmdletBinding()]
param(
  [string]$Exe = '',
  [ValidateSet('arm64', 'x64')]
  [string]$Arch = $(if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }),
  [string]$Configuration = 'Release',
  [string]$OutputDir = '',
  [switch]$PublishFirst
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) {
  $Exe = Join-Path $root "app\Wavee\bin\$Configuration\net10.0\win-$Arch\publish\Wavee.exe"
}
if (-not $OutputDir) { $OutputDir = Join-Path $root 'artifacts\bench' }

function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }

if ($PublishFirst) {
  & (Join-Path $PSScriptRoot 'publish-wavee-aot.ps1') -Arch $Arch -Configuration $Configuration
}
if (-not (Test-Path $Exe)) {
  throw "Wavee.exe not found at $Exe. Run publish-wavee-aot.cmd first or pass -PublishFirst."
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$env:WAVEE_BENCH_OUT = $OutputDir

Step "Running perf bench: $Exe"
$log = Join-Path $OutputDir 'wavee-perf-run.log'
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
  & $Exe --fake --perf-bench *>&1 | Tee-Object -FilePath $log | Out-Host
} finally {
  $ErrorActionPreference = $prevEap
}

$jsonPath = Join-Path $OutputDir 'wavee-perf-latest.json'
if (-not (Test-Path $jsonPath)) { throw "Perf bench did not write $jsonPath (see $log)." }
$txtPath = Join-Path $OutputDir 'wavee-perf-latest.txt'
if (Test-Path $txtPath) {
  Write-Host ''
  Get-Content $txtPath
}
if (Test-Path $jsonPath) {
  Write-Host ''
  Write-Host "JSON: $jsonPath" -ForegroundColor Green
}
