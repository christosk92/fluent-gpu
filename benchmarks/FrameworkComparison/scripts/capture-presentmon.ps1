[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Executable,
    [Parameter(Mandatory)] [string]$Framework,
    [Parameter(Mandatory)] [string]$Scenario,
    [Parameter(Mandatory)] [string]$OutputDirectory,
    [ValidateSet('cpu', 'cadence')]
    [string]$Pass = 'cadence',
    [int]$Iterations = 1500,
    [int]$Warmup = 120,
    # Optional override only. When omitted, refresh is measured from the capture (never assume 99 Hz).
    [double]$RefreshHz = 0,
    # FluentGPU only: retain one zero-allocation per-frame JSONL trace beside the PresentMon capture.
    [switch]$PacingTrace,
    [string]$PresentMon = 'presentmon.exe'
)

$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path $Executable).Path
$out = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $out | Out-Null

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$safeFramework = ($Framework -replace '[^\w\-]+', '-').ToLowerInvariant()
$base = "$safeFramework-$Scenario-presentmon-$stamp"
$hostJson = Join-Path $out ($base + '-host.json')
$pacingTraceJsonl = $(if ($PacingTrace -and $Framework -eq 'FluentGpu') { Join-Path $out ($base + '-pacing.jsonl') } else { $null })
$csv = Join-Path $out ($base + '.csv')
$summaryJson = Join-Path $out ($base + '-summary.json')

function Get-Percentile([double[]]$values, [double]$p) {
    if ($null -eq $values -or $values.Length -eq 0) { return $null }
    $sorted = @($values | Sort-Object)
    $rank = [Math]::Max(0, [Math]::Min($sorted.Count - 1, [int][Math]::Ceiling(($p / 100.0) * $sorted.Count) - 1))
    return [double]$sorted[$rank]
}

function Get-Column([object[]]$rows, [string[]]$names) {
    if ($rows.Count -eq 0) { return @() }
    foreach ($name in $names) {
        if ($rows[0].PSObject.Properties.Name -contains $name) {
            return @(
                $rows |
                    ForEach-Object { $_.$name } |
                    Where-Object { $_ -ne $null -and "$_" -ne '' -and "$_" -ne 'NA' } |
                    ForEach-Object { [double]$_ }
            )
        }
    }
    return @()
}

function Get-StringColumn([object[]]$rows, [string[]]$names) {
    if ($rows.Count -eq 0) { return @() }
    foreach ($name in $names) {
        if ($rows[0].PSObject.Properties.Name -contains $name) {
            return @($rows | ForEach-Object { [string]$_.$name } | Where-Object { $_ -and $_ -ne 'NA' })
        }
    }
    return @()
}

function Get-NominalDisplayMode {
    # EnumDisplaySettings of the primary device — the Windows-reported mode, which may disagree with DWM cadence.
    $sig = @'
using System;
using System.Runtime.InteropServices;
public static class NativeDisplay {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  public struct DEVMODE {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmDeviceName;
    public short dmSpecVersion, dmDriverVersion; public short dmSize, dmDriverExtra; public int dmFields;
    public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
    public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmFormName;
    public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
  }
  [DllImport("user32.dll", CharSet=CharSet.Unicode)]
  public static extern bool EnumDisplaySettingsW(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);
}
'@
    try {
        if (-not ('NativeDisplay' -as [type])) { Add-Type -TypeDefinition $sig -ErrorAction Stop }
        $dm = New-Object -TypeName 'NativeDisplay+DEVMODE'
        $dm.dmSize = [Runtime.InteropServices.Marshal]::SizeOf(([type]'NativeDisplay+DEVMODE'))
        if ([NativeDisplay]::EnumDisplaySettingsW($null, -1, [ref]$dm)) {
            return [ordered]@{
                width = $dm.dmPelsWidth
                height = $dm.dmPelsHeight
                reportedHz = $dm.dmDisplayFrequency
                source = 'EnumDisplaySettingsW(ENUM_CURRENT_SETTINGS)'
            }
        }
    } catch { }
    return [ordered]@{ width = $null; height = $null; reportedHz = $null; source = 'unavailable' }
}

function Classify-MissedVblanks(
    [double[]]$between,
    [double]$measuredRefreshMs,
    [object[]]$rows
) {
    # Prefer DXGI refresh-count deltas when PresentMon exposes them; otherwise >1.5× measured refresh.
    # Ordinary 8.4–9.2 ms jitter around an ~8.33 ms cadence is NOT a miss.
    $refreshCountNames = @('PresentRefreshCount', 'SyncRefreshCount', 'RefreshCount')
    $counts = @()
    foreach ($name in $refreshCountNames) {
        if ($rows.Count -gt 0 -and $rows[0].PSObject.Properties.Name -contains $name) {
            $counts = @(
                $rows |
                    ForEach-Object { $_.$name } |
                    Where-Object { $_ -ne $null -and "$_" -ne '' -and "$_" -ne 'NA' } |
                    ForEach-Object { [uint64]$_ }
            )
            if ($counts.Count -gt 1) { break }
            $counts = @()
        }
    }

    $method = 'interval-1.5x-fallback'
    $missed = 0
    $engineCorrelated = 0
    if ($counts.Count -gt 1) {
        $method = 'dxgi-refresh-count-delta'
        for ($i = 1; $i -lt $counts.Count; $i++) {
            if ($counts[$i] -gt $counts[$i - 1]) {
                $slots = [int64]($counts[$i] - $counts[$i - 1] - 1)
                if ($slots -gt 0) {
                    $missed += [int]$slots
                    $engineCorrelated += [int]$slots
                }
            }
        }
    }
    elseif ($between.Count -gt 0 -and $measuredRefreshMs -gt 0) {
        $threshold = 1.5 * $measuredRefreshMs
        $missed = @($between | Where-Object { $_ -gt $threshold }).Count
        $engineCorrelated = $missed
    }

    return [ordered]@{
        method = $method
        missedVblanks = $missed
        engineCorrelatedMissedVblanks = $engineCorrelated
        thresholdMs = $(if ($measuredRefreshMs -gt 0) { [Math]::Round(1.5 * $measuredRefreshMs, 4) } else { $null })
        note = 'Do not treat ordinary sub-threshold interval jitter as a missed frame. DWM-global stalls are reported separately.'
    }
}

if (-not (Get-Command $PresentMon -ErrorAction SilentlyContinue)) {
    throw "PresentMon was not found ($PresentMon). Install PresentMon 2.x and ensure it is on PATH."
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$inPerfLogUsers = (@(whoami /groups 2>$null) -join "`n") -match 'Performance Log Users'
if (-not $isAdmin -and -not $inPerfLogUsers) {
    throw @'
PresentMon needs an elevated ARM64 PowerShell, or membership in "Performance Log Users".
'@
}

$nominal = Get-NominalDisplayMode
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    & $PresentMon --terminate_existing_session --session_name FrameworkComparison 1>$null 2>$null
} finally {
    $ErrorActionPreference = $prevEap
}

$benchArgs = @(
    '--scenario', $Scenario,
    '--output', $hostJson,
    '--pass', $Pass,
    '--iterations', $Iterations,
    '--warmup', $Warmup
)
if ($pacingTraceJsonl) { $benchArgs += @('--pacing-trace', $pacingTraceJsonl) }
$bench = Start-Process -FilePath $exe -ArgumentList $benchArgs -PassThru

$pmArgs = @(
    '--process_id', "$($bench.Id)",
    '--output_file', $csv,
    '--qpc_time',
    '--exclude_dropped',
    '--stop_existing_session',
    '--session_name', 'FrameworkComparison',
    '--terminate_on_proc_exit',
    '--no_console_stats'
)
$pmErr = Join-Path $out ($base + '-presentmon.err.txt')
$pm = Start-Process -FilePath $PresentMon -ArgumentList $pmArgs -PassThru -WindowStyle Hidden -RedirectStandardError $pmErr
Start-Sleep -Milliseconds 500

try {
    $bench.WaitForExit()
    $exitCode = $bench.ExitCode
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    while (-not $pm.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 200
    }
}
finally {
    if (-not $pm.HasExited) {
        Stop-Process -Id $pm.Id -Force -ErrorAction SilentlyContinue
    }
    $ErrorActionPreference = 'Continue'
    try {
        & $PresentMon --terminate_existing_session --session_name FrameworkComparison 1>$null 2>$null
    } finally {
        $ErrorActionPreference = $prevEap
    }
}

$actualCsv = $csv
if (-not (Test-Path $actualCsv)) {
    $candidate = Get-ChildItem -Path $out -Filter ($base + '*.csv') | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($candidate) { $actualCsv = $candidate.FullName }
}
if (-not (Test-Path $actualCsv)) {
    throw "PresentMon did not write $csv (benchmark exit=$exitCode)."
}

$rows = @(Import-Csv -Path $actualCsv)
if ($rows.Count -eq 0) { throw "PresentMon CSV is empty: $actualCsv" }

$between = Get-Column $rows @('MsBetweenPresents', 'msBetweenPresents')
$displayed = Get-Column $rows @('MsBetweenDisplayChange', 'msBetweenDisplayChange')
$gpuBusy = Get-Column $rows @('MsGPUBusy', 'msGPUBusy')
$gpuTime = Get-Column $rows @('MsGPUTime', 'msGPUTime')
$untilDisplay = Get-Column $rows @('MsUntilDisplayed', 'msUntilDisplayed')
$cpuBusy = Get-Column $rows @('MsCPUBusy', 'msCPUBusy')
$renderPresentLatency = Get-Column $rows @('MsRenderPresentLatency', 'msRenderPresentLatency')
$appStart = Get-Column $rows @('MsBetweenAppStart', 'msBetweenAppStart')
$presentModes = Get-StringColumn $rows @('PresentMode', 'presentMode')

# Measured refresh: prefer display-change p50 (what was shown), else present p50, else nominal override/report.
$measuredRefreshMs = $null
if ($displayed.Count -ge 8) { $measuredRefreshMs = Get-Percentile $displayed 50 }
elseif ($between.Count -ge 8) { $measuredRefreshMs = Get-Percentile $between 50 }
elseif ($RefreshHz -gt 0) { $measuredRefreshMs = 1000.0 / $RefreshHz }
elseif ($nominal.reportedHz -gt 1) { $measuredRefreshMs = 1000.0 / [double]$nominal.reportedHz }

$measuredHz = if ($measuredRefreshMs -and $measuredRefreshMs -gt 0) { 1000.0 / $measuredRefreshMs } else { $null }
$nominalHz = if ($nominal.reportedHz) { [double]$nominal.reportedHz } else { $null }
$conflict = $false
$conflictNote = $null
if ($nominalHz -and $measuredHz) {
    $ratio = $measuredHz / $nominalHz
    if ($ratio -lt 0.92 -or $ratio -gt 1.08) {
        $conflict = $true
        $conflictNote = "Windows reports ${nominalHz} Hz but measured display-change cadence is $([Math]::Round($measuredHz,2)) Hz. Do not claim the Windows figure until resolved."
    }
}

$missed = Classify-MissedVblanks -between $between -measuredRefreshMs ($(if ($measuredRefreshMs) { $measuredRefreshMs } else { 0 })) -rows $rows
$over1_5 = if ($measuredRefreshMs) { @($between | Where-Object { $_ -gt (1.5 * $measuredRefreshMs) }).Count } else { $null }
$over2_0 = if ($measuredRefreshMs) { @($between | Where-Object { $_ -gt (2.0 * $measuredRefreshMs) }).Count } else { $null }
# Jitter band around measured refresh — informational only, never counted as misses.
$jitterBand = $null
if ($measuredRefreshMs) {
    $lo = $measuredRefreshMs * 0.95
    $hi = $measuredRefreshMs * 1.15
    $inBand = @($between | Where-Object { $_ -ge $lo -and $_ -le $hi }).Count
    $jitterBand = [ordered]@{
        lowMs = [Math]::Round($lo, 4)
        highMs = [Math]::Round($hi, 4)
        inBandCount = $inBand
        note = 'Sub-threshold jitter (e.g. 8.4–9.2 ms around ~8.33 ms) is expected and is not a missed vblank.'
    }
}

$displayedFps = if ($displayed.Count -gt 1) {
    1000.0 / (($displayed | Measure-Object -Average).Average)
} elseif ($between.Count -gt 1) {
    1000.0 / (($between | Measure-Object -Average).Average)
} else { $null }

$modeGroups = @($presentModes | Group-Object | Sort-Object Count -Descending | ForEach-Object {
    [ordered]@{ mode = $_.Name; count = $_.Count }
})

$summary = [ordered]@{
    schema = 'fluentgpu-framework-presentmon/v2'
    framework = $Framework
    scenario = $Scenario
    pass = $Pass
    architecture = $env:PROCESSOR_ARCHITECTURE
    hostExitCode = $exitCode
    hostResultPath = $(if (Test-Path $hostJson) { $hostJson } else { $null })
    pacingTracePath = $(if ($pacingTraceJsonl -and (Test-Path $pacingTraceJsonl)) { $pacingTraceJsonl } else { $null })
    presentMonCsv = $actualCsv
    presentedFrames = $rows.Count
    scope = [ordered]@{
        appOwned = 'PresentMon rows filtered to the benchmark PID only'
        dwmGlobal = 'Not mixed into app missed-vblank totals; use WPR/DWM counters for compositor-global stalls'
    }
    display = [ordered]@{
        nominal = $nominal
        measuredRefreshMs = $measuredRefreshMs
        measuredHz = $measuredHz
        refreshSource = $(if ($displayed.Count -ge 8) { 'MsBetweenDisplayChange.p50' }
            elseif ($between.Count -ge 8) { 'MsBetweenPresents.p50' }
            elseif ($RefreshHz -gt 0) { 'caller-override' }
            else { 'nominal-or-unavailable' })
        vrrOrConflict = $conflict
        conflictNote = $conflictNote
        presentModes = $modeGroups
    }
    displayedFpsAverage = $displayedFps
    missedVblanks = $missed
    intervalsOver1_5Refresh = $over1_5
    intervalsOver2_0Refresh = $over2_0
    jitterBand = $jitterBand
    msBetweenPresents = @{
        count = $between.Count
        p50 = Get-Percentile $between 50
        p90 = Get-Percentile $between 90
        p95 = Get-Percentile $between 95
        p99 = Get-Percentile $between 99
        max = $(if ($between.Count) { ($between | Measure-Object -Maximum).Maximum } else { $null })
    }
    msBetweenDisplayChange = @{
        count = $displayed.Count
        p50 = Get-Percentile $displayed 50
        p95 = Get-Percentile $displayed 95
        p99 = Get-Percentile $displayed 99
        max = $(if ($displayed.Count) { ($displayed | Measure-Object -Maximum).Maximum } else { $null })
    }
    msUntilDisplayed = @{
        count = $untilDisplay.Count
        p50 = Get-Percentile $untilDisplay 50
        p99 = Get-Percentile $untilDisplay 99
        max = $(if ($untilDisplay.Count) { ($untilDisplay | Measure-Object -Maximum).Maximum } else { $null })
    }
    gpuBusyMs = @{
        count = $gpuBusy.Count
        p50 = Get-Percentile $gpuBusy 50
        p99 = Get-Percentile $gpuBusy 99
        max = $(if ($gpuBusy.Count) { ($gpuBusy | Measure-Object -Maximum).Maximum } else { $null })
    }
    gpuTimeMs = @{
        count = $gpuTime.Count
        p50 = Get-Percentile $gpuTime 50
        p99 = Get-Percentile $gpuTime 99
        max = $(if ($gpuTime.Count) { ($gpuTime | Measure-Object -Maximum).Maximum } else { $null })
    }
    cpuBusyMs = @{
        count = $cpuBusy.Count
        p50 = Get-Percentile $cpuBusy 50
        p99 = Get-Percentile $cpuBusy 99
        max = $(if ($cpuBusy.Count) { ($cpuBusy | Measure-Object -Maximum).Maximum } else { $null })
    }
    msRenderPresentLatency = @{
        count = $renderPresentLatency.Count
        p50 = Get-Percentile $renderPresentLatency 50
        p99 = Get-Percentile $renderPresentLatency 99
        max = $(if ($renderPresentLatency.Count) { ($renderPresentLatency | Measure-Object -Maximum).Maximum } else { $null })
    }
    msBetweenAppStart = @{
        count = $appStart.Count
        p50 = Get-Percentile $appStart 50
        p99 = Get-Percentile $appStart 99
        max = $(if ($appStart.Count) { ($appStart | Measure-Object -Maximum).Maximum } else { $null })
    }
    acceptanceHint = [ordered]@{
        atVerified120Hz = [ordered]@{
            displayedFpsMin = 119.5
            presentIntervalP95MsMax = 9.6
            presentIntervalP99MsMax = 10.5
            engineCorrelatedMissedVblanksMax = 0
            noIntervalAboveMs = 12.5
        }
        note = 'A 120 FPS claim is valid only after nominal vs measured refresh conflict is resolved. External DWM/OS stalls are reported separately.'
    }
    warning = 'In-app frameMs/cpuWorkMs are not substitutes for these PresentMon fields. Smoke hosts are non-publishable until Release NativeAOT hashes match.'
}

$summary | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 $summaryJson
Write-Output $summaryJson
if ($exitCode -ne 0) {
    throw "Benchmark exited with 0x$([uint32]$exitCode). PresentMon summary was still written."
}
