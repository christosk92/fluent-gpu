[CmdletBinding()]
param(
    [string]$PublishRoot = 'C:\WAVEE\fluent-gpu\artifacts\framework-comparison\publish',
    [string]$OutputDirectory = 'C:\WAVEE\fluent-gpu\benchmarks\FrameworkComparison\results\present',
    [string[]]$Scenarios = @('virtual-scroll-10k', 'localized-transform', 'localized-text', 'tree-churn'),
    [int]$Iterations = 1500,
    [int]$Warmup = 120,
    # 0 = measure from capture (required). Pass a value only as an explicit override.
    [double]$RefreshHz = 0,
    [switch]$PacingTrace,
    [switch]$AllowNonAot
)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'run-present-pass.ps1 must be launched from an elevated PowerShell so PresentMon can start its ETW session.'
}

$benchRoot = Split-Path -Parent $PSScriptRoot
$capture = Join-Path $PSScriptRoot 'capture-presentmon.ps1'
$fluent = Join-Path $PublishRoot 'FluentGpu\FluentGpuBench.exe'
$winUI = Join-Path $PublishRoot 'WinUI\WinUIBench.exe'

if (-not (Test-Path $fluent) -or -not (Test-Path $winUI)) {
    if (-not $AllowNonAot) {
        throw "NativeAOT hosts missing under $PublishRoot. Build with build-release.ps1, or pass -AllowNonAot for local bin paths."
    }
    $fluent = Join-Path $benchRoot 'FluentGpuBench\bin\Release\net10.0\win-arm64\FluentGpuBench.exe'
    $winUI = Join-Path $benchRoot 'WinUIBench\bin\Release\net10.0-windows10.0.26100.0\win-arm64\WinUIBench.exe'
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$summaries = [Collections.Generic.List[string]]::new()
foreach ($scenario in $Scenarios) {
    foreach ($pair in @(
        @{ Framework = 'FluentGpu'; Exe = $fluent },
        @{ Framework = 'WinUI 3'; Exe = $winUI }
    )) {
        Write-Host "PresentMon $($pair.Framework) / $scenario"
        $path = & $capture `
            -Executable $pair.Exe `
            -Framework $pair.Framework `
            -Scenario $scenario `
            -OutputDirectory $OutputDirectory `
            -Pass cadence `
            -Iterations $Iterations `
            -Warmup $Warmup `
            -RefreshHz $RefreshHz `
            -PacingTrace:($PacingTrace -and $pair.Framework -eq 'FluentGpu')
        $summaries.Add([string]$path)
    }
}

$index = Join-Path $OutputDirectory 'index.json'
[ordered]@{
    schema = 'fluentgpu-framework-present-index/v2'
    generatedUtc = [DateTimeOffset]::UtcNow
    refreshHzOverride = $RefreshHz
    refreshNote = 'Per-summary display.measuredHz is authoritative; override 0 means measured from capture.'
    summaries = @($summaries)
} | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 $index
Write-Output $index
