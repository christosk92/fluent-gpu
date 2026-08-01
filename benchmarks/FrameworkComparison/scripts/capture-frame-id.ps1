[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Executable,
    [Parameter(Mandatory)] [string]$Framework,
    [Parameter(Mandatory)] [string]$Scenario,
    [Parameter(Mandatory)] [string]$OutputDirectory,
    [ValidateSet('cpu', 'cadence')]
    [string]$Pass = 'cadence',
    [int]$Iterations = 400,
    [int]$Warmup = 60,
    [int]$SampleMilliseconds = 1
)

$ErrorActionPreference = 'Stop'
$benchRoot = Split-Path -Parent $PSScriptRoot
$captureProj = Join-Path $benchRoot 'Bench.FrameIdCapture\Bench.FrameIdCapture.csproj'
$out = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $out | Out-Null

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$safeFramework = ($Framework -replace '[^\w\-]+', '-').ToLowerInvariant()
$hostJson = Join-Path $out "$safeFramework-$Scenario-frameid-$stamp.json"

dotnet run --project $captureProj -c Release --no-launch-profile -- `
    --exe (Resolve-Path $Executable).Path `
    --framework $Framework `
    --scenario $Scenario `
    --output $hostJson `
    --pass $Pass `
    --iterations $Iterations `
    --warmup $Warmup `
    --sample-ms $SampleMilliseconds

if ($LASTEXITCODE -ne 0) {
    throw "Frame-ID capture failed with exit code $LASTEXITCODE. See $hostJson*.visibility.json / mutation logs."
}

$visibility = [IO.Path]::ChangeExtension($hostJson, '.visibility.json')
Write-Output $visibility
