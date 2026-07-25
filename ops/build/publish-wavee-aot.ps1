<#
.SYNOPSIS
  Publish Wavee as a NativeAOT single-file native exe.

.EXAMPLE
  ops\build\publish-wavee-aot.cmd
  pwsh ops/build/publish-wavee-aot.ps1
  pwsh ops/build/publish-wavee-aot.ps1 -Arch x64
  pwsh ops/build/publish-wavee-aot.ps1 -Diag     # diagnostics build (ScrollTrace + RenderBudget + FG_OPAQUE_WINDOW armed)

.NOTES
  -Diag defines FLUENTGPU_DIAG solution-wide (src/Directory.Build.props + src/apps/Directory.Build.props). It is a
  DIFFERENT BINARY from the shipping one: BindContract and BackwardsWriteGuard become default-ON once compiled in, so a
  feel-measurement session must clear them explicitly (FG_BIND_CONTRACT=0 FG_BACKWARDS_WRITE=0) — ops/diag does this.
  See ops/diag/README.md.
#>
[CmdletBinding()]
param(
  # Machine architecture from the ENVIRONMENT. RuntimeInformation.OSArchitecture is unreliable here: under Windows
  # PowerShell 5.1 (.NET Framework) an x64-emulated host on an ARM64 machine reports X64 for the OS, so publishing
  # from an emulated shell would quietly produce a win-x64 build on an ARM64 box. PROCESSOR_ARCHITEW6432 exists only
  # inside an emulated/WOW process and always names the REAL machine, so it wins when present.
  [ValidateSet('arm64', 'x64')]
  [string]$Arch = $(
    $a = $env:PROCESSOR_ARCHITEW6432
    if (-not $a) { $a = $env:PROCESSOR_ARCHITECTURE }
    if ("$a" -match 'ARM64') { 'arm64' } else { 'x64' }),
  [string]$Configuration = 'Release',
  [switch]$Symbols,
  [switch]$Diag
)
$ErrorActionPreference = 'Stop'

# Script lives at ops/build/ — repo root is two levels up.
$root   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$csproj = Join-Path $root 'src\apps\Wavee\Wavee.csproj'
$rid    = "win-$Arch"
$outDir = if ($Symbols) {
  Join-Path $root "src\apps\Wavee\bin\publish-aot-symbols"
} elseif ($Diag) {
  # Its own tree: a diag exe must never silently replace the shipping publish an operator then measures as "Release".
  Join-Path $root "src\apps\Wavee\bin\publish-aot-diag\$rid"
} else {
  Join-Path $root "src\apps\Wavee\bin\$Configuration\net10.0\$rid\publish"
}
$exe    = Join-Path $outDir 'Wavee.exe'

function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }

# ILC needs link.exe via vswhere on PATH.
$vsInstaller = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer'
if ((Test-Path "$vsInstaller\vswhere.exe") -and ($env:PATH -notlike "*$vsInstaller*")) {
  $env:PATH = "$vsInstaller;$env:PATH"
}

# Keep MSBuild/VBCSCompiler temp under the repo (short path, no roaming-profile locks).
$tmp = Join-Path $root '.tmp-msbuild'
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$env:TEMP = $tmp
$env:TMP  = $tmp

Step "Publishing Wavee NativeAOT ($rid, $Configuration, OptimizationPreference=Speed$(if ($Symbols) { ', NativeDebugSymbols' })$(if ($Diag) { ', FLUENTGPU_DIAG' }))"
$pubArgs = @(
  $csproj, '-c', $Configuration, '-r', $rid,
  '/p:NuGetAudit=false', '/p:OptimizationPreference=Speed',
  '-o', $outDir, '--nologo'
)
if ($Symbols) {
  $pubArgs += '/p:NativeDebugSymbols=true', '/p:DebugType=portable', '/p:IlcGenerateMapFile=true'
}
if ($Diag) {
  $pubArgs += '/p:FluentGpuDiag=true'
}
& dotnet publish @pubArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

if (-not (Test-Path $exe)) { throw "Expected output not found: $exe" }
$info = Get-Item $exe
Write-Host ""
$ver = (Select-String -Path $csproj -Pattern '<InformationalVersion>([^<]+)</InformationalVersion>').Matches[0].Groups[1].Value
Write-Host "Done: $($info.FullName)" -ForegroundColor Green
Write-Host "      v$ver  $([math]::Round($info.Length / 1MB, 2)) MB"
if ($Diag) {
  # ASCII only inside string literals here: this file has no BOM, so Windows PowerShell 5.1 decodes it as ANSI and a
  # non-ASCII character in a QUOTED STRING is a parse error that kills the whole script. (Comments survive it, which
  # is why the em-dash on line 27 has always been fine and this one was not.)
  Write-Host "      FLUENTGPU_DIAG build - NOT the shipping binary. Clear FG_BIND_CONTRACT/FG_BACKWARDS_WRITE when measuring." -ForegroundColor Yellow
}
if ($Symbols) {
  $pdb = Join-Path $outDir 'Wavee.pdb'
  if (Test-Path $pdb) {
    $pdbInfo = Get-Item $pdb
    Write-Host "      PDB: $($pdbInfo.FullName)  $([math]::Round($pdbInfo.Length / 1MB, 2)) MB" -ForegroundColor Green
    Write-Host ""
    Write-Host "WinDbg: .sympath+ $outDir" -ForegroundColor DarkGray
  }
}
