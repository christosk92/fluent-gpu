[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Executable,
    [Parameter(Mandatory)] [string]$Framework,
    [Parameter(Mandatory)] [string]$Scenario,
    [Parameter(Mandatory)] [string]$Output,
    [int]$Iterations = 1500,
    [int]$Warmup = 120,
    [int]$SampleMilliseconds = 250
)

$ErrorActionPreference = 'Stop'
$exe = Resolve-Path $Executable
$outputPath = [IO.Path]::GetFullPath($Output)
$outputDir = Split-Path -Parent $outputPath
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$hostResult = Join-Path $outputDir ([IO.Path]::GetFileNameWithoutExtension($outputPath) + '-host.json')

function Read-CounterSum([string]$Path) {
    try {
        $sample = Get-Counter -Counter $Path -ErrorAction Stop
        return [double](($sample.CounterSamples | Measure-Object -Property CookedValue -Sum).Sum)
    }
    catch { return 0.0 }
}

$adapterSharedBefore = Read-CounterSum '\GPU Adapter Memory(*)\Shared Usage'
$adapterDedicatedBefore = Read-CounterSum '\GPU Adapter Memory(*)\Dedicated Usage'
$process = Start-Process -FilePath $exe -ArgumentList @(
    '--scenario', $Scenario,
    '--output', $hostResult,
    '--pass', 'cadence',
    '--iterations', $Iterations,
    '--warmup', $Warmup
) -PassThru

$samples = [Collections.Generic.List[object]]::new()
while (-not $process.HasExited) {
    $process.Refresh()
    $pidPattern = "pid_$($process.Id)_*"
    $samples.Add([pscustomobject]@{
        timestampUtc = [DateTimeOffset]::UtcNow
        workingSetBytes = [long]$process.WorkingSet64
        privateBytes = [long]$process.PrivateMemorySize64
        gpuSharedBytes = [long](Read-CounterSum "\GPU Process Memory($pidPattern)\Shared Usage")
        gpuDedicatedBytes = [long](Read-CounterSum "\GPU Process Memory($pidPattern)\Dedicated Usage")
        gpuLocalBytes = [long](Read-CounterSum "\GPU Process Memory($pidPattern)\Local Usage")
        gpuNonLocalBytes = [long](Read-CounterSum "\GPU Process Memory($pidPattern)\Non Local Usage")
        adapterSharedDeltaBytes = [long]((Read-CounterSum '\GPU Adapter Memory(*)\Shared Usage') - $adapterSharedBefore)
        adapterDedicatedDeltaBytes = [long]((Read-CounterSum '\GPU Adapter Memory(*)\Dedicated Usage') - $adapterDedicatedBefore)
    })
    Start-Sleep -Milliseconds $SampleMilliseconds
}

$document = [ordered]@{
    schema = 'fluentgpu-framework-gpu-memory/v1'
    framework = $Framework
    scenario = $Scenario
    architecture = $env:PROCESSOR_ARCHITECTURE
    unifiedMemoryArchitecture = $true
    warning = 'Do not sum process working set and GPU shared usage; pages can overlap on UMA. Adapter deltas also include system noise.'
    exitCode = $process.ExitCode
    samples = $samples
}
$document | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 $outputPath
Write-Output $outputPath
