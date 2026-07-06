# Installs a PlayPlay runtime (Spotify.dll + playplay-runtime.json) into the canonical store.
# Usage: .\scripts\install-playplay-runtime.ps1 [-SourceDir <path>]
# Env: WAVEE_PLAYPLAY_RUNTIME_SRC — default source directory when -SourceDir is omitted.

param(
    [string]$SourceDir = $env:WAVEE_PLAYPLAY_RUNTIME_SRC
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($SourceDir)) {
    Write-Error "Set -SourceDir or WAVEE_PLAYPLAY_RUNTIME_SRC to a folder containing Spotify.dll and playplay-runtime.json."
}

$SourceDir = (Resolve-Path $SourceDir).Path
$dll = Join-Path $SourceDir "Spotify.dll"
$manifest = Join-Path $SourceDir "playplay-runtime.json"

if (-not (Test-Path $dll)) { Write-Error "Spotify.dll not found in $SourceDir" }
if (-not (Test-Path $manifest)) { Write-Error "playplay-runtime.json not found in $SourceDir" }

$wavee = Join-Path $PSScriptRoot ".." "app" "Wavee" "Wavee.csproj"
if (-not (Test-Path $wavee)) { Write-Error "Wavee.csproj not found at $wavee" }

Write-Host "Registering PlayPlay runtime from $SourceDir ..."
dotnet run --project $wavee -- --playplay-runtime-register $SourceDir
exit $LASTEXITCODE
