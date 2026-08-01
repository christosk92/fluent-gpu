[CmdletBinding()]
param(
    [string]$OutputRoot = 'C:\WAVEE\fluent-gpu\artifacts\framework-comparison\publish',
    [string]$WinUIVersion = '2.3.1'
)

$ErrorActionPreference = 'Stop'
$benchRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Resolve-Path (Join-Path $benchRoot '..\..')

$fluentOut = Join-Path $OutputRoot 'FluentGpu'
$winUIOut = Join-Path $OutputRoot 'WinUI'
dotnet publish (Join-Path $benchRoot 'FluentGpuBench\FluentGpuBench.csproj') -c Release -r win-arm64 -o $fluentOut
if ($LASTEXITCODE -ne 0) { throw 'FluentGPU NativeAOT publish failed.' }
dotnet publish (Join-Path $benchRoot 'WinUIBench\WinUIBench.csproj') -c Release -r win-arm64 -o $winUIOut --force -p:WindowsAppSdkVersion=$WinUIVersion
if ($LASTEXITCODE -ne 0) { throw 'WinUI NativeAOT publish failed.' }
dotnet build (Join-Path $benchRoot 'Bench.Runner\Bench.Runner.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Benchmark runner build failed.' }

$publishedWinUI = Join-Path $winUIOut 'Microsoft.UI.Xaml.dll'
if (-not (Test-Path $publishedWinUI)) { throw "Published WinUI binary is missing: $publishedWinUI" }
$publishedHash = (Get-FileHash -Algorithm SHA256 $publishedWinUI).Hash

$fluentExe = Join-Path $fluentOut 'FluentGpuBench.exe'
$winUiExe = Join-Path $winUIOut 'WinUIBench.exe'
$evidencePath = Join-Path $OutputRoot 'publish-evidence.json'
[ordered]@{
    schema = 'fluentgpu-framework-publish-evidence/v2'
    generatedUtc = [DateTimeOffset]::UtcNow
    fluentGpu = [ordered]@{
        executable = $fluentExe
        executableSha256 = (Get-FileHash -Algorithm SHA256 $fluentExe).Hash.ToLowerInvariant()
        commit = (git -C $repoRoot rev-parse HEAD).Trim()
    }
    winUi = [ordered]@{
        executable = $winUiExe
        executableSha256 = (Get-FileHash -Algorithm SHA256 $winUiExe).Hash.ToLowerInvariant()
        package = [ordered]@{
            id = 'Microsoft.WindowsAppSDK'
            version = $WinUIVersion
            source = 'https://api.nuget.org/v3/index.json'
            channel = 'stable-public'
        }
        publishedDll = $publishedWinUI
        publishedDllSha256 = $publishedHash.ToLowerInvariant()
    }
} | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 $evidencePath

[pscustomobject]@{
    FluentGpuExe = Join-Path $fluentOut 'FluentGpuBench.exe'
    WinUIExe = Join-Path $winUIOut 'WinUIBench.exe'
    WinUIPackage = "Microsoft.WindowsAppSDK $WinUIVersion"
    WinUIPublishedSha256 = $publishedHash.ToLowerInvariant()
    FluentGpuCommit = (git -C $repoRoot rev-parse HEAD).Trim()
    Evidence = $evidencePath
} | Format-List
