# Copies the installed Spotify.dll into the local audio-runtime pack cache (gitignored binary).
# Run after Spotify updates; re-hash manifest.json sha256_hex if the DLL changes.
param(
    [string]$SpotifyDll = (Join-Path $env:APPDATA "Spotify\Spotify.dll"),
    [string]$PackRoot = (Join-Path $PSScriptRoot "..\..\src\apps\audio-runtime\packs"),
    [switch]$AlsoCopyToWaveeOutput
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path $SpotifyDll)) {
    Write-Error "Spotify.dll not found at $SpotifyDll"
}

$destDir = (Resolve-Path (New-Item -ItemType Directory -Force $PackRoot)).Path
$dest = Join-Path $destDir "129300667-arm64.bin"
Copy-Item -LiteralPath $SpotifyDll -Destination $dest -Force

$hash = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLowerInvariant()
$ver = (Get-Item $SpotifyDll).VersionInfo.FileVersion
Write-Host "Copied Spotify.dll v$ver -> $dest"
Write-Host "SHA256: $hash"
Write-Host "Manifest expects: cf95a21911e932cb3a53ae5732c5f8f416b885d79e8cf793107d79e329e9b387"
if ($hash -ne "cf95a21911e932cb3a53ae5732c5f8f416b885d79e8cf793107d79e329e9b387") {
    Write-Warning "Hash mismatch - update src/apps/audio-runtime/manifest.json sha256_hex before provisioning."
}

if ($AlsoCopyToWaveeOutput) {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
    $outRoots = @(
        (Join-Path $repoRoot "src\apps\Wavee\bin\Debug\net10.0\audio-runtime\packs"),
        (Join-Path $repoRoot "src\apps\Wavee\bin\Release\net10.0\audio-runtime\packs")
    )
    foreach ($out in $outRoots) {
        if (-not (Test-Path (Split-Path $out -Parent))) { continue }
        New-Item -ItemType Directory -Force $out | Out-Null
        Copy-Item -LiteralPath $dest -Destination (Join-Path $out "129300667-arm64.bin") -Force
        $outFile = Join-Path $out "129300667-arm64.bin"
        Write-Host "Also copied -> $outFile"
    }
}
