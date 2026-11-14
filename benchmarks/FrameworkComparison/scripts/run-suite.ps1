[CmdletBinding()]
param(
    [string]$PublishRoot = '',
    [string]$Output = '',
    [ValidateSet('cpu', 'cadence', 'allocation')]
    [string]$Pass = 'cpu',
    [int]$Iterations = 1000,
    [int]$Warmup = 60,
    [int]$Repetitions = 5,
    [int]$StartupRepetitions = 30,
    [int]$LoadRepetitions = 10,
    # Optional command prefix that wraps EACH runner invocation (one per scenario), e.g. a machine-wide GPU lock:
    # -Wrap 'C:\Program Files\Git\bin\bash.exe','C:/WAVEE/scratch/gpulock.sh'
    [string[]]$Wrap = @()
)

$ErrorActionPreference = 'Stop'
$benchRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Resolve-Path (Join-Path $benchRoot '..\..')
if (-not $PublishRoot) { $PublishRoot = Join-Path $repoRoot 'artifacts\framework-comparison\publish' }
if (-not $Output) { $Output = Join-Path $benchRoot ('results\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $Pass) }
$fluent = Join-Path $PublishRoot 'FluentGpu\FluentGpuBench.exe'
$winUI = Join-Path $PublishRoot 'WinUI\WinUIBench.exe'
$evidencePath = Join-Path $PublishRoot 'publish-evidence.json'
$runner = Join-Path $benchRoot 'Bench.Runner\bin\Release\net11.0\Bench.Runner.exe'

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

# One runner invocation per scenario (each writes summary-<scenario>.json; alternating launch order inside it), so a
# machine-wide lock can be held per invocation instead of around the whole pass. Desktop-driven scenarios
# (window-resize, theme-switch) only exist on the cadence pass; the runner skips them on the others.
$scenarios = @('startup', 'buttons-225', 'text-1125', 'virtual-scroll-1k', 'virtual-scroll-10k', 'localized-transform',
    'localized-text', 'tree-churn', 'page-navigation', 'window-resize', 'theme-switch')
$common = @('--fluent', $fluent, '--winui', $winUI, '--output', $Output, '--pass', $Pass,
    '--iterations', $Iterations, '--warmup', $Warmup, '--repetitions', $Repetitions,
    '--startup-repetitions', $StartupRepetitions, '--load-repetitions', $LoadRepetitions,
    '--build-evidence', $evidencePath)
$failed = 0
foreach ($scenario in $scenarios) {
    $runArgs = $common + @('--scenarios', $scenario, '--summary-name', "summary-$scenario")
    if ($Wrap.Count -gt 0) { & $Wrap[0] ($Wrap[1..($Wrap.Count - 1)] + @($runner) + $runArgs) }
    else { & $runner @runArgs }
    if ($LASTEXITCODE -ne 0) { $failed++ }
}
& $runner @($common + @('--aggregate'))
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
exit $(if ($failed -eq 0) { 0 } else { 2 })
