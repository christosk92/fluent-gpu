<#
.SYNOPSIS
  Gate for the `fluentgpu-app` dotnet new template + FluentGpu.App.props/targets (app-authoring plan §7).

.DESCRIPTION
  1. Installs templates/fluentgpu-app into a PRIVATE template hive (--debug:custom-hive) under a temp directory, so the
     user's own `dotnet new` template list is never touched.
  2. Instantiates it there (`dotnet new fluentgpu-app --EnginePath <this checkout>`).
  3. Builds the generated app (Debug by default) and checks the exe landed in bin\.
  4. Optional -Publish: a NativeAOT `dotnet publish -c Release` (exercises PublishAot, the publish RID default and the
     vswhere PATH fix before the native link).
  The temp directory is removed afterwards unless -KeepTemp. Exit code 0 = pass.

  The generated app builds the engine projects of THIS checkout (their obj/bin), like any consumer.

.EXAMPLE
  powershell -File ops/scripts/check-app-template.ps1
  powershell -File ops/scripts/check-app-template.ps1 -Publish -KeepTemp
#>
[CmdletBinding()]
param(
  [string]$EngineRoot = '',
  [string]$Configuration = 'Debug',
  [string]$AppName = 'TemplateCheckApp',
  [switch]$Publish,
  [switch]$KeepTemp
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Resolved here, not as the param default: Windows PowerShell 5.1 leaves $PSScriptRoot empty while binding defaults when
# the script is started with -File from another shell.
if (-not $EngineRoot) {
  $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
  $EngineRoot = (Resolve-Path (Join-Path $scriptDir '..\..')).Path
}

function Invoke-Step {
  param([string]$Title, [string[]]$Arguments, [string]$WorkingDirectory)
  Write-Host ""
  Write-Host "==> $Title" -ForegroundColor Cyan
  Write-Host "    dotnet $($Arguments -join ' ')"
  Push-Location $WorkingDirectory
  try {
    & dotnet @Arguments
    $code = $LASTEXITCODE
  } finally {
    Pop-Location
  }
  if ($code -ne 0) { throw "$Title failed (exit $code)." }
}

$EngineRoot = (Resolve-Path $EngineRoot).Path.TrimEnd('\')
$templateDir = Join-Path $EngineRoot 'templates\fluentgpu-app'
foreach ($required in @((Join-Path $EngineRoot 'FluentGpu.App.props'),
                        (Join-Path $EngineRoot 'FluentGpu.App.targets'),
                        (Join-Path $templateDir '.template.config\template.json'))) {
  if (-not (Test-Path $required)) { throw "Missing $required" }
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("fluentgpu-template-check-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$hive = Join-Path $work 'hive'
$appDir = Join-Path $work $AppName
New-Item -ItemType Directory -Force -Path $hive | Out-Null

$failed = $false
try {
  Write-Host "Engine checkout : $EngineRoot"
  Write-Host "Work directory  : $work"

  Invoke-Step 'Install the template (private hive)' `
    @('new', 'install', $templateDir, '--debug:custom-hive', $hive) $work

  Invoke-Step 'Instantiate fluentgpu-app' `
    @('new', 'fluentgpu-app', '-n', $AppName, '-o', $appDir, '--EnginePath', $EngineRoot, '--debug:custom-hive', $hive) $work

  # The template must have substituted the engine path and the project name.
  $dbp = Get-Content (Join-Path $appDir 'Directory.Build.props') -Raw
  if ($dbp -match 'FLUENTGPU_ENGINE_ROOT') { throw 'EngineRoot was not substituted into Directory.Build.props.' }
  $csproj = Join-Path $appDir "$AppName.csproj"
  if (-not (Test-Path $csproj)) { throw "sourceName was not applied: $csproj is missing." }
  foreach ($f in @('Program.cs', 'App.cs', '.gitignore', 'global.json')) {
    if (-not (Test-Path (Join-Path $appDir $f))) { throw "Template output is missing $f." }
  }

  Invoke-Step "Build ($Configuration)" @('build', $csproj, '-c', $Configuration, '-nologo') $appDir

  $exe = Get-ChildItem -Path (Join-Path $appDir "bin\$Configuration") -Recurse -Filter "$AppName.exe" -ErrorAction SilentlyContinue |
         Select-Object -First 1
  if (-not $exe) { throw "Build succeeded but bin\$Configuration\**\$AppName.exe was not produced." }
  Write-Host "    built: $($exe.FullName)"

  if ($Publish) {
    $publishDir = Join-Path $appDir 'publish'
    Invoke-Step 'NativeAOT publish (Release)' @('publish', $csproj, '-c', 'Release', '-o', $publishDir, '-nologo') $appDir
    $native = Join-Path $publishDir "$AppName.exe"
    if (-not (Test-Path $native)) { throw "Publish succeeded but $native was not produced." }
    if (Test-Path (Join-Path $publishDir "$AppName.dll")) { throw "Publish produced $AppName.dll: PublishAot did not engage." }
    Write-Host "    native: $native ($([math]::Round((Get-Item $native).Length / 1MB, 1)) MB)"
  }

  Write-Host ""
  Write-Host "TEMPLATE CHECK PASSED" -ForegroundColor Green
} catch {
  $failed = $true
  Write-Host ""
  Write-Host "TEMPLATE CHECK FAILED: $($_.Exception.Message)" -ForegroundColor Red
} finally {
  if ($KeepTemp) {
    Write-Host "Kept: $work"
  } else {
    Remove-Item -Recurse -Force -Path $work -ErrorAction SilentlyContinue
  }
}

if ($failed) { exit 1 }
exit 0
