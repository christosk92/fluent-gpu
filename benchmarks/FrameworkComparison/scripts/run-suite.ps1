[CmdletBinding()]
param(
    [string]$PublishRoot = 'C:\WAVEE\fluent-gpu\artifacts\framework-comparison\publish',
    [string]$Output = ('C:\WAVEE\fluent-gpu\benchmarks\FrameworkComparison\results\' + (Get-Date -Format 'yyyyMMdd-HHmmss')),
    [ValidateSet('cpu', 'cadence', 'allocation')]
    [string]$Pass = 'cpu',
    [int]$Iterations = 1000,
    [int]$Warmup = 60,
    [int]$Repetitions = 5,
    [int]$StartupRepetitions = 30,
    [int]$LoadRepetitions = 10
)

$ErrorActionPreference = 'Stop'
$benchRoot = Split-Path -Parent $PSScriptRoot
$fluent = Join-Path $PublishRoot 'FluentGpu\FluentGpuBench.exe'
$winUI = Join-Path $PublishRoot 'WinUI\WinUIBench.exe'
$evidencePath = Join-Path $PublishRoot 'publish-evidence.json'
$runner = Join-Path $benchRoot 'Bench.Runner\Bench.Runner.csproj'

if (-not (Test-Path $evidencePath)) {
    throw "Release evidence is missing: $evidencePath. Run build-release.ps1 before a publishable suite."
}
if (-not (Test-Path $fluent) -or -not (Test-Path $winUI)) {
    throw 'Published benchmark hosts are missing. Run build-release.ps1 before run-suite.ps1.'
}
$evidence = Get-Content -Raw $evidencePath | ConvertFrom-Json
$actualFluent = (Get-FileHash -Algorithm SHA256 $fluent).Hash.ToLowerInvariant()
$actualWinUI = (Get-FileHash -Algorithm SHA256 $winUI).Hash.ToLowerInvariant()
if ($actualFluent -ne $evidence.fluentGpu.executableSha256 -or $actualWinUI -ne $evidence.winUi.executableSha256) {
    throw 'Published host hash differs from publish-evidence.json. Re-run build-release.ps1; do not mix binaries and evidence.'
}

dotnet run --project $runner -c Release --no-build -- `
    --fluent $fluent `
    --winui $winUI `
    --output $Output `
    --pass $Pass `
    --iterations $Iterations `
    --warmup $Warmup `
    --repetitions $Repetitions `
    --startup-repetitions $StartupRepetitions `
    --load-repetitions $LoadRepetitions `
    --build-evidence $evidencePath
exit $LASTEXITCODE
