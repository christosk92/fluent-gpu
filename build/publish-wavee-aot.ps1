<#
.SYNOPSIS
  Publish Wavee as a NativeAOT single-file native exe.

.EXAMPLE
  build\publish-wavee-aot.cmd
  pwsh build/publish-wavee-aot.ps1
  pwsh build/publish-wavee-aot.ps1 -Arch x64
#>
[CmdletBinding()]
param(
  [ValidateSet('arm64', 'x64')]
  [string]$Arch = $(if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }),
  [string]$Configuration = 'Release',
  [switch]$Symbols
)
$ErrorActionPreference = 'Stop'

$root   = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root 'app\Wavee\Wavee.csproj'
$rid    = "win-$Arch"
$outDir = if ($Symbols) {
  Join-Path $root "app\Wavee\bin\publish-aot-symbols"
} else {
  Join-Path $root "app\Wavee\bin\$Configuration\net10.0\$rid\publish"
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

Step "Publishing Wavee NativeAOT ($rid, $Configuration, OptimizationPreference=Speed$(if ($Symbols) { ', NativeDebugSymbols' }))"
$pubArgs = @(
  $csproj, '-c', $Configuration, '-r', $rid,
  '/p:NuGetAudit=false', '/p:OptimizationPreference=Speed',
  '-o', $outDir, '--nologo'
)
if ($Symbols) {
  $pubArgs += '/p:NativeDebugSymbols=true', '/p:DebugType=portable', '/p:IlcGenerateMapFile=true'
}
& dotnet publish @pubArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

if (-not (Test-Path $exe)) { throw "Expected output not found: $exe" }
$info = Get-Item $exe
Write-Host ""
$ver = (Select-String -Path $csproj -Pattern '<InformationalVersion>([^<]+)</InformationalVersion>').Matches[0].Groups[1].Value
Write-Host "Done: $($info.FullName)" -ForegroundColor Green
Write-Host "      v$ver  $([math]::Round($info.Length / 1MB, 2)) MB"
if ($Symbols) {
  $pdb = Join-Path $outDir 'Wavee.pdb'
  if (Test-Path $pdb) {
    $pdbInfo = Get-Item $pdb
    Write-Host "      PDB: $($pdbInfo.FullName)  $([math]::Round($pdbInfo.Length / 1MB, 2)) MB" -ForegroundColor Green
    Write-Host ""
    Write-Host "WinDbg: .sympath+ $outDir" -ForegroundColor DarkGray
  }
}
