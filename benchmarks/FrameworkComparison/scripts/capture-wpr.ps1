[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Executable,
    [Parameter(Mandatory)] [string]$Framework,
    [Parameter(Mandatory)] [string]$Scenario,
    [Parameter(Mandatory)] [string]$OutputDirectory,
    [ValidateSet('CPU', 'GPU', 'ResidentSet', 'Heap', 'XAMLActivity', 'XAMLAppResponsiveness')]
    [string]$Profile = 'CPU',
    [int]$Iterations = 1500,
    [int]$Warmup = 120
)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'WPR capture requires an elevated ARM64 PowerShell. Re-run this script as Administrator.'
}

$exe = Resolve-Path $Executable
$out = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $out | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$base = "$($Framework.Replace(' ', '-'))-$Scenario-$Profile-$stamp"
$etl = Join-Path $out ($base + '.etl')
$hostJson = Join-Path $out ($base + '-host.json')

wpr.exe -cancel 2>$null
wpr.exe -start $Profile -filemode
if ($LASTEXITCODE -ne 0) { throw "WPR could not start profile $Profile." }
try {
    $process = Start-Process -FilePath $exe -ArgumentList @(
        '--scenario', $Scenario,
        '--output', $hostJson,
        '--pass', 'cadence',
        '--iterations', $Iterations,
        '--warmup', $Warmup
    ) -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Benchmark exited with 0x$([uint32]$process.ExitCode)." }
}
finally {
    wpr.exe -stop $etl "Framework comparison: $Framework / $Scenario / $Profile"
}
Write-Output $etl
